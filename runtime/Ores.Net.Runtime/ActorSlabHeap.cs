using System.Runtime.InteropServices;

namespace Ores.Net.Runtime;

/// <summary>
/// Bounded, size-class slab allocator for one actor incarnation: the .NET twin
/// of atto-source.llvm <c>runtime/ActorSlabHeap.hpp</c> and atto-source.rs
/// <c>src/actor_slab_heap.rs</c>.
///
/// Arena bytes AND page metadata live in <see cref="NativeMemory"/>, so actor
/// objects add nothing to the CLR heap and nothing scales with object count on
/// the GC. Guest references are 4-byte words on every host:
/// <list type="bullet">
/// <item><b>Offset32</b> = <c>offset / 8 + 1</c> (the cross-backend word): no
/// per-object metadata, for compiler-proven actor-local lifetimes. Rejects
/// null, foreign, interior, unallocated and freed words, not ABA.</item>
/// <item><b>Handle32</b> = <c>(generation &lt;&lt; slotBits) | (slot + 1)</c>
/// through an 8-byte table entry: capacity-sized slot bits, FIFO reuse,
/// exhausted slots quarantined, never wrapped.</item>
/// </list>
/// Allocation decisions mirror the C++ and Rust heaps exactly, so
/// <see cref="GoldenScriptV1"/> must reproduce the shared fixture
/// <c>actor_slab_heap_golden_v1.txt</c>. Memory is committed in granules up to
/// a budget (growth may move the block; references are offsets, so they stay
/// valid). Every failure returns a status and leaves the heap unchanged.
/// Single owner: the actor turn holding this heap. Not thread-safe.
/// </summary>
public unsafe sealed class ActorSlabHeap : IDisposable
{
    public const int PageBytes = 4096;
    public const int HeaderBytes = 64;
    public const int MaxSmall = 2048;
    public const int MaxAlignment = 64;
    public static ReadOnlySpan<ushort> ClassBytes => new ushort[]
    {
        16, 32, 48, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512,
        640, 768, 896, 1024, 1280, 1536, 1792, 2048,
    };
    private const int Classes = 24;
    private const int BitmapBits = (HeaderBytes - 16) * 8; // same as the C++ header
    private const uint None = 0;
    private const ulong MaxSpan = (ulong)uint.MaxValue * 8;

    private enum Kind : byte { Unused, Small, Large, LargeTail, Free, FreeTail, Meta }

    [StructLayout(LayoutKind.Sequential)]
    private struct Page
    {
        public Kind Kind;
        public byte Cls;
        public ushort Live, FreeHead, Bump;
        public uint Prev, Next, Run;
        public fixed ulong Bitmap[BitmapBits / 64];
    }

    private byte* _mem;
    private nuint _committed;
    private Page* _pages;
    private uint _pageSlots;
    private readonly nuint _reserve, _budget, _granule;
    private readonly uint _maxPages;
    private uint _highWater, _freeRuns;
    // One 24-entry table per heap (not per object): partial-page list heads.
    private readonly uint[] _partial = new uint[Classes];
    private nuint _handleBase;
    private uint _handleCapacity, _slotBits, _maxGen, _freeSlotHead, _freeSlotTail, _quarantined;
    private bool _retired;
    private SlabStats _stats;

    public ActorSlabHeap(ActorSlabHeapConfig config)
    {
        var reserve = config.ReserveBytes / PageBytes * PageBytes;
        if (reserve == 0 || config.BudgetBytes == 0 || config.BudgetBytes > reserve
                || (ulong)reserve > MaxSpan || reserve / PageBytes > uint.MaxValue - 1)
            throw new ArgumentException("invalid actor slab heap configuration", nameof(config));
        _reserve = reserve;
        _budget = config.BudgetBytes;
        _granule = Math.Max(config.CommitGranule, (nuint)PageBytes) / PageBytes * PageBytes;
        _maxPages = (uint)(reserve / PageBytes);
        _stats = new SlabStats { ReservedBytes = reserve, MetadataBytes = _maxPages };
        if (config.HandleCapacity != 0 && InitHandles(config.HandleCapacity, config.HandleGenerationBits) is { } error)
        {
            Dispose();
            throw new ArgumentException($"invalid Handle32 profile: {error}", nameof(config));
        }
    }

    public SlabStats Stats => _stats;
    public uint HandleSlotBits => _slotBits;
    public uint HandleMaxGeneration => _maxGen;

    private SlabStatus? InitHandles(uint capacity, uint genBitsCfg)
    {
        uint bits = 0;
        while ((1UL << (int)bits) <= capacity) bits++;
        if (bits >= 31) return SlabStatus.InvalidConfig;
        var genBits = 32 - bits;
        if (genBitsCfg != 0)
        {
            if (genBitsCfg < 2 || genBitsCfg > genBits) return SlabStatus.InvalidConfig;
            genBits = genBitsCfg;
        }
        var count = (uint)(((nuint)capacity * 8 + HeaderBytes + PageBytes - 1) / PageBytes);
        var s = TakePages(count, out var first);
        if (s != SlabStatus.Ok) return s;
        _pages[first] = new Page { Kind = Kind.Meta, Run = count };
        for (uint p = 1; p < count; p++) _pages[first + p].Kind = Kind.LargeTail;
        _handleBase = (nuint)first * PageBytes + HeaderBytes;
        _handleCapacity = capacity;
        _slotBits = bits;
        _maxGen = (uint)((1UL << (int)genBits) - 1);
        for (uint i = 0; i < capacity; i++) WriteEntry(i, i + 1 < capacity ? i + 2 : None, 1);
        _freeSlotHead = 1;
        _freeSlotTail = capacity;
        _stats.MetadataBytes += (nuint)count * PageBytes;
        _stats.LivePages += count;
        return null;
    }

    // ---- memory -------------------------------------------------------------
    private SlabStatus CommitThrough(nuint end)
    {
        if (end <= _committed) return SlabStatus.Ok;
        if (end > _reserve) return SlabStatus.OutOfMemory;
        var target = Math.Min((end + _granule - 1) / _granule * _granule, _reserve);
        if (target > _budget)
        {
            if (end > _budget) return SlabStatus.BudgetExceeded;
            target = _budget;
        }
        var grown = (byte*)NativeMemory.Realloc(_mem, target);
        if (grown == null) return SlabStatus.OutOfMemory; // old block untouched
        NativeMemory.Clear(grown + _committed, target - _committed);
        _mem = grown;
        _committed = target;
        _stats.CommittedBytes = target;
        return SlabStatus.Ok;
    }

    private SlabStatus EnsurePageSlots(uint needed)
    {
        if (needed <= _pageSlots) return SlabStatus.Ok;
        var slots = Math.Min(Math.Max(needed, _pageSlots * 2), _maxPages);
        var grown = (Page*)NativeMemory.Realloc(_pages, (nuint)slots * (nuint)sizeof(Page));
        if (grown == null) return SlabStatus.OutOfMemory;
        NativeMemory.Clear(grown + _pageSlots, (nuint)(slots - _pageSlots) * (nuint)sizeof(Page));
        _pages = grown;
        _pageSlots = slots;
        return SlabStatus.Ok;
    }

    private SlabStatus TakePages(uint count, out uint first)
    {
        for (var link = _freeRuns; link != None; link = _pages[link - 1].Next)
        {
            var head = link - 1;
            var have = _pages[head].Run;
            if (have < count) continue;
            UnlinkFreeRun(head);
            if (have > count) PushFreeRun(head + count, have - count);
            _stats.FreePages -= count;
            first = head;
            return SlabStatus.Ok;
        }
        first = 0;
        if (count > _maxPages - _highWater) return SlabStatus.OutOfMemory;
        var s = EnsurePageSlots(_highWater + count);
        if (s != SlabStatus.Ok) return s;
        s = CommitThrough((nuint)(_highWater + count) * PageBytes);
        if (s != SlabStatus.Ok) return s;
        first = _highWater;
        _highWater += count;
        return SlabStatus.Ok;
    }

    private void PushFreeRun(uint first, uint count)
    {
        var after = first + count;
        if (after < _highWater && _pages[after].Kind == Kind.Free)
        {
            var more = _pages[after].Run;
            UnlinkFreeRun(after);
            count += more;
        }
        _pages[first] = new Page { Kind = Kind.Free, Run = count, Next = _freeRuns, Prev = None };
        for (uint p = 1; p < count; p++) _pages[first + p].Kind = Kind.FreeTail;
        if (_freeRuns != None) _pages[_freeRuns - 1].Prev = first + 1;
        _freeRuns = first + 1;
    }

    private void UnlinkFreeRun(uint first)
    {
        var (prev, next) = (_pages[first].Prev, _pages[first].Next);
        if (prev != None) _pages[prev - 1].Next = next; else _freeRuns = next;
        if (next != None) _pages[next - 1].Prev = prev;
        _pages[first].Kind = Kind.Unused;
    }

    private void PartialPush(byte cls, uint index)
    {
        var head = _partial[cls];
        _pages[index].Prev = None;
        _pages[index].Next = head;
        if (head != None) _pages[head - 1].Prev = index + 1;
        _partial[cls] = index + 1;
    }

    private void PartialRemove(byte cls, uint index)
    {
        var (prev, next) = (_pages[index].Prev, _pages[index].Next);
        if (prev != None) _pages[prev - 1].Next = next; else _partial[cls] = next;
        if (next != None) _pages[next - 1].Prev = prev;
        _pages[index].Prev = _pages[index].Next = None;
    }

    private static bool Bit(Page* p, int i) => ((p->Bitmap[i >> 6] >> (i & 63)) & 1) == 1;
    private static void SetBit(Page* p, int i, bool on)
    {
        if (on) p->Bitmap[i >> 6] |= 1UL << (i & 63);
        else p->Bitmap[i >> 6] &= ~(1UL << (i & 63));
    }
    private static ushort BlocksInPage(int size) => (ushort)Math.Min((PageBytes - HeaderBytes) / size, BitmapBits);

    private static int ClassFor(nuint bytes, nuint align)
    {
        var classes = ClassBytes;
        for (var c = 0; c < Classes; c++)
            if (classes[c] >= bytes && classes[c] % align == 0) return c;
        return -1;
    }

    private static bool Encode(nuint offset, out uint word)
    {
        word = 0;
        if (offset % 8 != 0 || (ulong)(offset / 8) >= uint.MaxValue) return false;
        word = (uint)(offset / 8) + 1;
        return true;
    }

    // ---- Offset32 -----------------------------------------------------------
    public SlabStatus Allocate(nuint bytes, nuint align, out uint word)
    {
        word = 0;
        if (_retired) return SlabStatus.Retired;
        if (bytes == 0) return SlabStatus.InvalidSize;
        if (align == 0 || (align & (align - 1)) != 0 || align > MaxAlignment) return SlabStatus.InvalidAlignment;
        if (align < 8) align = 8;
        var c = bytes <= MaxSmall ? ClassFor(bytes, align) : -1;
        if (c < 0) return AllocateLarge(bytes, out word);
        var cls = (byte)c;
        uint index;
        if (_partial[cls] != None)
        {
            index = _partial[cls] - 1;
        }
        else
        {
            var s = TakePages(1, out index);
            if (s != SlabStatus.Ok) return s;
            _pages[index] = new Page { Kind = Kind.Small, Cls = cls };
            PartialPush(cls, index);
            _stats.MetadataBytes += HeaderBytes;
            _stats.LivePages += 1;
        }
        var size = ClassBytes[cls];
        var pageBase = (nuint)index * PageBytes + HeaderBytes;
        var page = &_pages[index];
        int block;
        if (page->FreeHead != 0)
        {
            block = page->FreeHead - 1;
            page->FreeHead = *(ushort*)(_mem + pageBase + (nuint)(block * size)); // little-endian hosts
        }
        else
        {
            block = page->Bump;
            page->Bump++;
        }
        SetBit(page, block, true);
        page->Live++;
        if (page->FreeHead == 0 && page->Bump == BlocksInPage(size)) PartialRemove(cls, index);
        var offset = pageBase + (nuint)(block * size);
        NativeMemory.Clear(_mem + offset, size);
        _stats.LiveBlockBytes += size;
        _stats.LiveObjects += 1;
        return Encode(offset, out word) ? SlabStatus.Ok : SlabStatus.InvalidRef;
    }

    private SlabStatus AllocateLarge(nuint bytes, out uint word)
    {
        word = 0;
        if (bytes > _reserve) return SlabStatus.OutOfMemory;
        var count = (uint)((bytes + HeaderBytes + PageBytes - 1) / PageBytes);
        var s = TakePages(count, out var first);
        if (s != SlabStatus.Ok) return s;
        _pages[first] = new Page { Kind = Kind.Large, Run = count };
        for (uint p = 1; p < count; p++) _pages[first + p].Kind = Kind.LargeTail;
        var payload = (nuint)count * PageBytes - HeaderBytes;
        var offset = (nuint)first * PageBytes + HeaderBytes;
        NativeMemory.Clear(_mem + offset, payload);
        _stats.MetadataBytes += HeaderBytes;
        _stats.LivePages += count;
        _stats.LiveBlockBytes += payload;
        _stats.LiveObjects += 1;
        return Encode(offset, out word) ? SlabStatus.Ok : SlabStatus.InvalidRef;
    }

    private SlabStatus Locate(uint word, out uint index, out int block, out nuint usable, out bool large)
    {
        index = 0; block = 0; usable = 0; large = false;
        if (_retired) return SlabStatus.Retired;
        if (word == 0) return SlabStatus.InvalidRef;
        var offset = ((ulong)word - 1) * 8;
        if (offset >= _reserve) return SlabStatus.InvalidRef;
        index = (uint)(offset / PageBytes);
        var within = (int)(offset % PageBytes);
        if (index >= _highWater || within < HeaderBytes) return SlabStatus.InvalidRef;
        var page = &_pages[index];
        switch (page->Kind)
        {
            case Kind.Small:
                int size = ClassBytes[page->Cls];
                if ((within - HeaderBytes) % size != 0) return SlabStatus.InvalidRef;
                block = (within - HeaderBytes) / size;
                if (block >= page->Bump || !Bit(page, block)) return SlabStatus.InvalidRef;
                usable = (nuint)size;
                return SlabStatus.Ok;
            case Kind.Large when within == HeaderBytes: // Meta is never an object
                block = (int)page->Run;
                usable = (nuint)page->Run * PageBytes - HeaderBytes;
                large = true;
                return SlabStatus.Ok;
            default:
                return SlabStatus.InvalidRef;
        }
    }

    public SlabStatus Free(uint word)
    {
        var s = Locate(word, out var index, out var block, out var usable, out var large);
        if (s != SlabStatus.Ok) return s;
        if (large)
        {
            var run = (uint)block;
            NativeMemory.Clear(_mem + (nuint)index * PageBytes + HeaderBytes, usable);
            PushFreeRun(index, run);
            _stats.FreePages += run;
            _stats.LivePages -= run;
            _stats.MetadataBytes -= HeaderBytes;
            _stats.LiveBlockBytes -= usable;
            _stats.LiveObjects -= 1;
            return SlabStatus.Ok;
        }
        var size = (int)usable;
        var at = _mem + (nuint)index * PageBytes + HeaderBytes + (nuint)(block * size);
        NativeMemory.Clear(at, usable);
        var page = &_pages[index];
        var wasFull = page->FreeHead == 0 && page->Bump == BlocksInPage(size);
        SetBit(page, block, false);
        page->Live--;
        _stats.LiveBlockBytes -= usable;
        _stats.LiveObjects -= 1;
        if (page->Live == 0)
        {
            if (!wasFull) PartialRemove(page->Cls, index);
            PushFreeRun(index, 1);
            _stats.FreePages += 1;
            _stats.LivePages -= 1;
            _stats.MetadataBytes -= HeaderBytes;
            return SlabStatus.Ok;
        }
        *(ushort*)at = page->FreeHead;
        page->FreeHead = (ushort)(block + 1);
        if (wasFull) PartialPush(page->Cls, index);
        return SlabStatus.Ok;
    }

    /// <summary>Checked view; <paramref name="length"/> must fit the live block. Do not keep the span across Allocate.</summary>
    public SlabStatus Bytes(uint word, int length, out Span<byte> bytes)
    {
        bytes = default;
        var s = Locate(word, out _, out _, out var usable, out _);
        if (s != SlabStatus.Ok) return s;
        if (length < 0 || (nuint)length > usable) return SlabStatus.InvalidSize;
        bytes = new Span<byte>(_mem + ((nuint)word - 1) * 8, length);
        return SlabStatus.Ok;
    }

    // ---- Handle32 -----------------------------------------------------------
    private (uint word, uint gen) Entry(uint slot)
    {
        var at = (uint*)(_mem + _handleBase + (nuint)slot * 8);
        return (at[0], at[1]);
    }
    private void WriteEntry(uint slot, uint word, uint gen)
    {
        var at = (uint*)(_mem + _handleBase + (nuint)slot * 8);
        at[0] = word;
        at[1] = gen;
    }

    public SlabStatus AllocateHandle(nuint bytes, nuint align, out uint handle)
    {
        handle = 0;
        if (_retired) return SlabStatus.Retired;
        if (_handleCapacity == 0 || _freeSlotHead == None) return SlabStatus.HandleTableFull;
        var s = Allocate(bytes, align, out var word);
        if (s != SlabStatus.Ok) return s;
        var slot = _freeSlotHead - 1;
        var (next, gen) = Entry(slot);
        _freeSlotHead = next;
        if (next == None) _freeSlotTail = None;
        WriteEntry(slot, word, gen);
        handle = (gen << (int)_slotBits) | (slot + 1);
        return SlabStatus.Ok;
    }

    public SlabStatus ResolveHandle(uint handle, out uint word)
    {
        word = 0;
        if (_retired) return SlabStatus.Retired;
        var slot1 = handle & ((1u << (int)_slotBits) - 1);
        if (_handleCapacity == 0 || slot1 == 0 || slot1 > _handleCapacity) return SlabStatus.InvalidRef;
        var (w, gen) = Entry(slot1 - 1);
        if (gen == 0 || (handle >> (int)_slotBits) != gen || w == 0) return SlabStatus.StaleHandle;
        word = w;
        return SlabStatus.Ok;
    }

    public SlabStatus FreeHandle(uint handle)
    {
        var s = ResolveHandle(handle, out var word);
        if (s != SlabStatus.Ok) return s;
        s = Free(word);
        if (s != SlabStatus.Ok) return s;
        var slot = (handle & ((1u << (int)_slotBits) - 1)) - 1;
        var (_, gen) = Entry(slot);
        if (gen == _maxGen)
        {
            WriteEntry(slot, None, 0); // quarantine: never wrap, never reuse
            _stats.QuarantinedHandles = ++_quarantined;
            return SlabStatus.Ok;
        }
        WriteEntry(slot, None, gen + 1);
        if (_freeSlotTail != None)
        {
            var tail = _freeSlotTail - 1;
            WriteEntry(tail, slot + 1, Entry(tail).gen);
        }
        else
        {
            _freeSlotHead = slot + 1;
        }
        _freeSlotTail = slot + 1;
        return SlabStatus.Ok;
    }

    /// <summary>Actor STOP: scrub and release native memory. Idempotent.</summary>
    public void Dispose()
    {
        if (_mem != null)
        {
            NativeMemory.Clear(_mem, _committed);
            NativeMemory.Free(_mem);
            _mem = null;
        }
        if (_pages != null)
        {
            NativeMemory.Free(_pages);
            _pages = null;
        }
        _committed = 0;
        _retired = true;
        _stats = default;
    }

    /// <summary>
    /// Cross-backend conformance script. Words per op: allocate → ref32 word;
    /// free → 0; allocate_handle → handle word then its resolved word;
    /// free_handle → 1. Must equal <c>actor_slab_heap_golden_v1.txt</c>.
    /// </summary>
    public static List<uint> GoldenScriptV1()
    {
        using var h = new ActorSlabHeap(new ActorSlabHeapConfig(16u << 20, 16u << 20) { HandleCapacity = 64 });
        var output = new List<uint>();
        var live = new List<uint>();
        var handles = new List<uint>();
        uint lcg = 7;
        uint Next() { lcg = unchecked(lcg * 1664525u + 1013904223u); return lcg >> 8; }
        nuint[] sizes = [8, 16, 24, 40, 64, 100, 200, 512, 1000, 2048, 3000, 9000];
        nuint[] aligns = [8, 16, 32, 64];
        static void SwapRemove(List<uint> list, int i) { list[i] = list[^1]; list.RemoveAt(list.Count - 1); }
        for (var op = 0; op < 2000; op++)
        {
            var v = Next() % 10;
            if (v <= 4)
            {
                var size = sizes[Next() % 12];
                var align = aligns[Next() % 4];
                if (h.Allocate(size, align, out var w) != SlabStatus.Ok) throw new InvalidOperationException("golden allocate");
                output.Add(w);
                live.Add(w);
            }
            else if ((v == 5 || v == 6) && live.Count > 0)
            {
                var i = (int)(Next() % (uint)live.Count);
                if (h.Free(live[i]) != SlabStatus.Ok) throw new InvalidOperationException("golden free");
                SwapRemove(live, i);
                output.Add(0);
            }
            else if (v == 7)
            {
                if (h.AllocateHandle(sizes[Next() % 6], 8, out var x) == SlabStatus.Ok)
                {
                    output.Add(x);
                    h.ResolveHandle(x, out var w);
                    output.Add(w);
                    handles.Add(x);
                }
            }
            else if (v == 8 && handles.Count > 0)
            {
                var i = (int)(Next() % (uint)handles.Count);
                if (h.FreeHandle(handles[i]) != SlabStatus.Ok) throw new InvalidOperationException("golden free_handle");
                SwapRemove(handles, i);
                output.Add(1);
            }
        }
        return output;
    }

    /// <summary>Canonical status codes shared by every backend (golden v2).</summary>
    public static uint Code(SlabStatus s) => s switch
    {
        SlabStatus.Ok => 0,
        SlabStatus.OutOfMemory => 1,
        SlabStatus.BudgetExceeded => 2,
        SlabStatus.InvalidSize => 3,
        SlabStatus.InvalidAlignment => 4,
        SlabStatus.InvalidRef => 5,
        SlabStatus.StaleHandle => 6,
        SlabStatus.HandleTableFull => 7,
        SlabStatus.Retired => 8,
        _ => 99,
    };

    /// <summary>
    /// Golden v2: failure paths and edge cases; same script as the C++
    /// <c>tests/actor_slab_heap_golden_v2_test.cpp</c> (see it for the format).
    /// </summary>
    public static List<uint> GoldenScriptV2()
    {
        using var h = new ActorSlabHeap(new ActorSlabHeapConfig(256 * 1024, 128 * 1024)
        {
            CommitGranule = 16 * 1024, HandleCapacity = 8, HandleGenerationBits = 3,
        });
        var output = new List<uint>();
        uint lcg = 99;
        uint Next() { lcg = unchecked(lcg * 1664525u + 1013904223u); return lcg >> 8; }
        static uint Fail(SlabStatus s) => 0x8000_0000u | Code(s);
        static void Remember(List<uint> v, uint w) { v.Add(w); if (v.Count > 8) v.RemoveAt(0); }
        static void SwapRemove(List<uint> v, int i) { v[i] = v[^1]; v.RemoveAt(v.Count - 1); }
        nuint[] sizes = [0, 1, 8, 16, 17, 33, 48, 63, 64, 65, 100, 127, 128, 129, 255, 256, 257, 500, 1000, 2047, 2048, 2049];
        nuint[] aligns = [1, 8, 16, 32, 64, 128, 3];
        nuint[] larges = [3000, 4033, 4032, 8000, 12000, 20000];
        List<uint> live = [], freed = [], handles = [], freedHandles = [];
        for (var op = 0; op < 3000; op++)
        {
            var v = Next() % 16;
            if (v <= 5)
            {
                var s = sizes[Next() % 22];
                var a = aligns[Next() % 7];
                var st = h.Allocate(s, a, out var w);
                if (st == SlabStatus.Ok) { output.Add(w); live.Add(w); } else output.Add(Fail(st));
            }
            else if (v <= 8)
            {
                if (live.Count == 0) continue;
                var i = (int)(Next() % (uint)live.Count);
                var w = live[i];
                var st = h.Free(w);
                output.Add(st == SlabStatus.Ok ? 0 : Fail(st));
                SwapRemove(live, i);
                Remember(freed, w);
            }
            else if (v == 9)
            {
                if (freed.Count == 0) continue;
                var w = freed[(int)(Next() % (uint)freed.Count)];
                var st = h.Free(w);
                output.Add(st == SlabStatus.Ok ? 0 : Fail(st));
                if (st == SlabStatus.Ok) { var i = live.IndexOf(w); if (i >= 0) SwapRemove(live, i); }
            }
            else if (v <= 11)
            {
                var st = h.AllocateHandle(sizes[1 + Next() % 12], 8, out var x);
                if (st == SlabStatus.Ok) { output.Add(x); handles.Add(x); } else output.Add(Fail(st));
            }
            else if (v <= 13)
            {
                if (handles.Count == 0) continue;
                var i = (int)(Next() % (uint)handles.Count);
                var w = handles[i];
                var st = h.FreeHandle(w);
                output.Add(st == SlabStatus.Ok ? 0 : Fail(st));
                SwapRemove(handles, i);
                Remember(freedHandles, w);
            }
            else if (v == 14)
            {
                if (freedHandles.Count == 0) continue;
                var st = h.ResolveHandle(freedHandles[(int)(Next() % (uint)freedHandles.Count)], out var w);
                output.Add(st == SlabStatus.Ok ? w : Fail(st));
            }
            else
            {
                var st = h.Allocate(larges[Next() % 6], 8, out var w);
                if (st == SlabStatus.Ok) { output.Add(w); live.Add(w); } else output.Add(Fail(st));
            }
        }
        var stats = h.Stats;
        output.AddRange([(uint)stats.LiveObjects, (uint)stats.LivePages, (uint)stats.FreePages,
            stats.QuarantinedHandles, (uint)stats.CommittedBytes]);
        return output;
    }
}


public enum SlabStatus : byte
{
    Ok, OutOfMemory, BudgetExceeded, InvalidSize, InvalidAlignment, InvalidRef,
    StaleHandle, HandleTableFull, InvalidConfig, Retired,
}

public sealed record ActorSlabHeapConfig(nuint ReserveBytes, nuint BudgetBytes)
{
    public nuint CommitGranule { get; init; } = 64 * 1024;
    public uint HandleCapacity { get; init; }
    /// <summary>0 = every bit not needed for the slot.</summary>
    public uint HandleGenerationBits { get; init; }
}

public struct SlabStats
{
    public nuint ReservedBytes, CommittedBytes, MetadataBytes, LiveBlockBytes;
    public nuint LiveObjects, LivePages, FreePages;
    public uint QuarantinedHandles;
}
