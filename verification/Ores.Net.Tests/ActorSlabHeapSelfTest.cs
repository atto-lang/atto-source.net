using Ores.Net.Runtime;

/// <summary>
/// ActorSlabHeap: 32-bit actor heap with the same words as the LLVM (C++) and
/// rs/wasm32 heaps. The golden fixture is shared byte-for-byte with
/// atto-source.llvm and atto-source.rs.
/// </summary>
internal static class ActorSlabHeapSelfTest
{
    public static void Run()
    {
        AllocateZeroedResolveAndDoubleFree();
        PackingAndCrossClassReuse();
        ForgedInteriorAndTailRefsFailClosed();
        BudgetFailureIsAtomic();
        LargeRequestsAndGranuleRespectNativeWidth();
        HandlesFifoStaleAndQuarantine();
        StressIntegrity();
        SmallClassSteadyStateAllocatesNoManagedBytes();
        GoldenMatchesCppAndRust();
        GoldenV2MatchesCppAndRust();
        Console.WriteLine("ActorSlabHeapSelfTest: PASS (Offset32/Handle32, golden v1 + v2)");
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("ActorSlabHeap: " + what);
    }

    private static ActorSlabHeap Heap(nuint reserve, nuint budget) => new(new ActorSlabHeapConfig(reserve, budget));

    private static void AllocateZeroedResolveAndDoubleFree()
    {
        using var h = Heap(1 << 20, 1 << 20);
        Check(h.Stats.CommittedBytes == 0, "commits nothing up front");
        Check(h.Allocate(24, 8, out var a) == SlabStatus.Ok, "allocate");
        Check(h.Bytes(a, 24, out var span) == SlabStatus.Ok && !span.ContainsAnyExcept((byte)0), "zeroed");
        span.Fill(0xAB);
        Check(h.Bytes(a, 32, out _) == SlabStatus.Ok, "class rounding");
        Check(h.Bytes(a, 33, out _) == SlabStatus.InvalidSize, "overrun rejected");
        Check(h.Free(a) == SlabStatus.Ok, "free");
        Check(h.Free(a) == SlabStatus.InvalidRef, "double free rejected");
        Check(h.Allocate(0, 8, out _) == SlabStatus.InvalidSize, "zero size");
        Check(h.Allocate(8, 3, out _) == SlabStatus.InvalidAlignment, "bad alignment");
        Check(h.Allocate(8, 128, out _) == SlabStatus.InvalidAlignment, "over-alignment");
        foreach (nuint align in new nuint[] { 8, 16, 32, 64 })
            foreach (nuint size in new nuint[] { 1, 17, 50, 100, 700, 2048 })
            {
                Check(h.Allocate(size, align, out var r) == SlabStatus.Ok, "aligned allocate");
                Check(((ulong)r - 1) * 8 % align == 0, "alignment honored");
                Check(h.Free(r) == SlabStatus.Ok, "aligned free");
            }
        Check(h.Stats.LiveObjects == 0, "all freed");
    }

    private static void PackingAndCrossClassReuse()
    {
        using var h = Heap(1 << 20, 1 << 20);
        var refs = new List<uint>();
        for (var i = 0; i < 253; i++) { h.Allocate(16, 8, out var r); refs.Add(r); }
        Check(h.Stats.LivePages == 2, "252 sixteen-byte objects per page");
        var committed = h.Stats.CommittedBytes;
        foreach (var r in refs) Check(h.Free(r) == SlabStatus.Ok, "free packed");
        Check(h.Stats.LivePages == 0 && h.Stats.FreePages == 2, "pages returned");
        for (var i = 0; i < 4; i++) Check(h.Allocate(1000, 8, out _) == SlabStatus.Ok, "reuse by another class");
        Check(h.Stats.CommittedBytes == committed, "no new commit");
    }

    private static void ForgedInteriorAndTailRefsFailClosed()
    {
        using var h = Heap(1 << 20, 1 << 20);
        h.Allocate(32, 8, out var a);
        foreach (var w in new[] { a + 1, 1u, uint.MaxValue, a + 512 })
            Check(h.Free(w) == SlabStatus.InvalidRef, $"forged word {w}");
        Check(h.Allocate(3 * 4096, 8, out var big) == SlabStatus.Ok, "large");
        h.Bytes(big, 3 * 4096, out var payload);
        payload.Fill(0x01);
        var tail = big + 4096 / 8;
        Check(h.Free(tail) == SlabStatus.InvalidRef, "tail-page ref rejected");
        Check(h.Free(big) == SlabStatus.Ok && h.Free(a) == SlabStatus.Ok, "frees");
    }

    private static void BudgetFailureIsAtomic()
    {
        using var h = Heap(1 << 20, 64 * 1024);
        var refs = new List<uint>();
        SlabStatus s;
        while ((s = h.Allocate(512, 8, out var r)) == SlabStatus.Ok) refs.Add(r);
        Check(s == SlabStatus.BudgetExceeded, "budget enforced");
        var before = h.Stats;
        Check(h.Allocate(100_000, 8, out _) == SlabStatus.BudgetExceeded, "large over budget");
        Check(h.Stats.LiveObjects == before.LiveObjects && h.Stats.LivePages == before.LivePages, "unchanged");
        Check(h.Free(refs[^1]) == SlabStatus.Ok && h.Allocate(512, 8, out _) == SlabStatus.Ok, "keeps working");
    }

    private static void LargeRequestsAndGranuleRespectNativeWidth()
    {
        // The same 4-GiB-minus-one-page reserve is representable on x86/arm32
        // and x64/arm64. Only 64 KiB is actually available to commit.
        nuint reserve = (nuint)0xFFFFF000u;
        using var h = new ActorSlabHeap(new ActorSlabHeapConfig(reserve, 64 * 1024)
        { CommitGranule = nuint.MaxValue });
        Check(h.Allocate(reserve, 8, out var bad) == SlabStatus.OutOfMemory && bad == 0,
            "large request must include its header within reserve");
        Check(h.Allocate(reserve - ActorSlabHeap.HeaderBytes, 8, out bad) == SlabStatus.BudgetExceeded && bad == 0,
            "large near-limit request must respect physical budget before commit");
        Check(h.Stats.LiveObjects == 0 && h.Stats.CommittedBytes == 0, "large refusal leaves no live allocation");
        Check(h.Allocate(16, 8, out var word) == SlabStatus.Ok, "small allocation after large refusal");
        Check(h.Stats.CommittedBytes == 64 * 1024, "commit granule is bounded by physical budget");
        Check(h.Free(word) == SlabStatus.Ok, "free after large refusal");
    }

    private static void HandlesFifoStaleAndQuarantine()
    {
        using var h = new ActorSlabHeap(new ActorSlabHeapConfig(1 << 20, 1 << 20) { HandleCapacity = 4 });
        Check(h.HandleSlotBits == 3 && h.HandleMaxGeneration == (1u << 29) - 1, "capacity-sized split");
        static uint Slot(uint x) => x & 7;
        h.AllocateHandle(40, 8, out var a);
        h.AllocateHandle(40, 8, out var b);
        Check(Slot(a) == 1 && Slot(b) == 2, "slots");
        Check(h.FreeHandle(a) == SlabStatus.Ok, "free handle");
        Check(h.ResolveHandle(a, out _) == SlabStatus.StaleHandle, "stale detected");
        h.AllocateHandle(40, 8, out var c);
        h.AllocateHandle(40, 8, out var d);
        h.AllocateHandle(40, 8, out var e);
        Check(Slot(c) == 3 && Slot(d) == 4 && Slot(e) == 1 && e != a, "FIFO reuse with new generation");
        Check(h.AllocateHandle(40, 8, out _) == SlabStatus.HandleTableFull, "table full");

        using var small = new ActorSlabHeap(new ActorSlabHeapConfig(1 << 20, 1 << 20) { HandleCapacity = 2, HandleGenerationBits = 2 });
        var n = 0;
        while (small.AllocateHandle(16, 8, out var x) == SlabStatus.Ok) { n++; small.FreeHandle(x); }
        Check(n == 6 && small.Stats.QuarantinedHandles == 2, "quarantine after 3 generations per slot");
        Check(small.Allocate(16, 8, out _) == SlabStatus.Ok, "Offset32 unaffected");
    }

    private static void StressIntegrity()
    {
        using var h = Heap(8 << 20, 8 << 20);
        var live = new List<(uint word, int bytes, byte tag)>();
        uint lcg = 12345;
        uint Next() { lcg = unchecked(lcg * 1664525u + 1013904223u); return lcg >> 8; }
        for (var op = 0; op < 100_000; op++)
        {
            if (live.Count == 0 || Next() % 100 < 55)
            {
                var bytes = Next() % 100 < 95 ? 1 + (int)(Next() % 256) : 2049 + (int)(Next() % 9000);
                if (h.Allocate((nuint)bytes, 8, out var w) != SlabStatus.Ok) continue;
                var tag = (byte)Next();
                h.Bytes(w, bytes, out var span);
                span.Fill(tag);
                live.Add((w, bytes, tag));
            }
            else
            {
                var i = (int)(Next() % (uint)live.Count);
                var (w, bytes, tag) = live[i];
                h.Bytes(w, bytes, out var span);
                Check(!span.ContainsAnyExcept(tag), "contents intact");
                Check(h.Free(w) == SlabStatus.Ok, "stress free");
                live[i] = live[^1];
                live.RemoveAt(live.Count - 1);
            }
        }
        foreach (var (w, _, _) in live) Check(h.Free(w) == SlabStatus.Ok, "final free");
        Check(h.Stats.LiveObjects == 0 && h.Stats.LivePages == 0, "empty at end");
    }

    private static void SmallClassSteadyStateAllocatesNoManagedBytes()
    {
        using var h = Heap(1 << 20, 1 << 20);
        static void Exercise(ActorSlabHeap heap, int repetitions)
        {
            for (var pass = 0; pass < repetitions; pass++)
                foreach (var size in ActorSlabHeap.ClassBytes)
                {
                    Check(heap.Allocate(size, 8, out var word) == SlabStatus.Ok, "allocation probe alloc");
                    Check(heap.Bytes(word, size, out var bytes) == SlabStatus.Ok, "allocation probe resolve");
                    bytes.Fill(0x2A);
                    Check(heap.Free(word) == SlabStatus.Ok, "allocation probe free");
                }
        }
        // Warm up all class paths and the accounting API outside measurement.
        Exercise(h, 32);
        _ = GC.GetAllocatedBytesForCurrentThread();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Exercise(h, 256);
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(delta == 0, $"steady-state slab operations allocated {delta} managed bytes");
        Check(h.Stats.LiveObjects == 0 && h.Stats.LivePages == 0, "allocation probe reclaimed all objects");
    }

    private static void GoldenMatchesCppAndRust()
    {
        var path = FindGolden("actor_slab_heap_golden_v1.txt");
        var expected = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();
        var actual = ActorSlabHeap.GoldenScriptV1();
        Check(actual.Count == expected.Count, $"golden length {actual.Count} != {expected.Count}");
        for (var i = 0; i < actual.Count; i++)
            Check(actual[i] == expected[i], $"golden word {i}: {actual[i]} != {expected[i]}");
    }

    private static void GoldenV2MatchesCppAndRust()
    {
        // v2 adds failure paths: canonical codes must agree across backends too.
        var expected = File.ReadAllText(FindGolden("actor_slab_heap_golden_v2.txt"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();
        var actual = ActorSlabHeap.GoldenScriptV2();
        Check(actual.Count == expected.Count, $"golden v2 length {actual.Count} != {expected.Count}");
        for (var i = 0; i < actual.Count; i++)
            Check(actual[i] == expected[i], $"golden v2 word {i}: {actual[i]} != {expected[i]}");
    }

    private static string FindGolden(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "actor-slab-heap", "v1", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("contracts/actor-slab-heap/v1/" + name);
    }
}
