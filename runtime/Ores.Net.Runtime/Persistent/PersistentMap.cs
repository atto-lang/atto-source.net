// Persistent immutable hash map (HAMT, 32-way, 5 bits per level).
// Every update returns a new map that shares all untouched nodes with the
// previous version; old versions remain valid and unchanged (snapshots are
// O(1)). Full hash collisions are stored in collision nodes, so a hostile
// or degenerate GetHashCode degrades to linear scans but never loses data.
// Iteration order is unspecified (it follows hash bits). No reflection,
// no locking: NativeAOT- and actor-safe because values are never mutated
// after construction.
using System.Collections;
using System.Numerics;

namespace Ores.Net.Runtime.Persistent;

public sealed class PersistentMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly Node _root;
    private readonly IEqualityComparer<TKey> _comparer;

    public int Count { get; }

    public static PersistentMap<TKey, TValue> Empty { get; } =
        new(BitmapNode.EmptyNode, 0, EqualityComparer<TKey>.Default);

    public static PersistentMap<TKey, TValue> WithComparer(IEqualityComparer<TKey> comparer) =>
        new(BitmapNode.EmptyNode, 0, comparer ?? throw new ArgumentNullException(nameof(comparer)));

    private PersistentMap(Node root, int count, IEqualityComparer<TKey> comparer)
    {
        _root = root;
        Count = count;
        _comparer = comparer;
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        var hash = _comparer.GetHashCode(key);
        return _root.TryGet(key, hash, 0, _comparer, out value);
    }

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public TValue this[TKey key] =>
        TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();

    public PersistentMap<TKey, TValue> SetItem(TKey key, TValue value)
    {
        var hash = _comparer.GetHashCode(key);
        var root = _root.Set(new Entry(key, value, hash), 0, _comparer, out var added);
        return ReferenceEquals(root, _root) ? this : new(root, added ? Count + 1 : Count, _comparer);
    }

    public PersistentMap<TKey, TValue> Remove(TKey key)
    {
        var hash = _comparer.GetHashCode(key);
        var root = _root.Remove(key, hash, 0, _comparer, out var removed);
        if (!removed) return this;
        return new(root as BitmapNode ?? BitmapNode.FromSingle(root, 0), Count - 1, _comparer);
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        foreach (var e in _root.Entries()) yield return new(e.Key, e.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Order-independent content equality (values via Default comparer).</summary>
    public bool ContentEquals(PersistentMap<TKey, TValue> other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other.Count != Count) return false;
        var values = EqualityComparer<TValue>.Default;
        foreach (var e in _root.Entries())
            if (!other.TryGetValue(e.Key, out var v) || !values.Equals(v, e.Value)) return false;
        return true;
    }

    internal sealed record Entry(TKey Key, TValue Value, int Hash);

    internal abstract class Node
    {
        public abstract bool TryGet(TKey key, int hash, int shift, IEqualityComparer<TKey> cmp, out TValue value);
        public abstract Node Set(Entry entry, int shift, IEqualityComparer<TKey> cmp, out bool added);
        /// <summary>Returns the replacement: a Node, a lone Entry (to inline in the parent), or null when empty.</summary>
        public abstract object? Remove(TKey key, int hash, int shift, IEqualityComparer<TKey> cmp, out bool removed);
        public abstract IEnumerable<Entry> Entries();
    }

    private static int Bit(int hash, int shift) => 1 << ((hash >>> shift) & 31);

    private static object Merge(Entry a, Entry b, int shift)
    {
        if (a.Hash == b.Hash || shift > 30) return new CollisionNode(a.Hash, new[] { a, b });
        int ba = Bit(a.Hash, shift), bb = Bit(b.Hash, shift);
        if (ba == bb) return new BitmapNode((uint)ba, new[] { Merge(a, b, shift + 5) });
        var slots = (uint)ba < (uint)bb ? new object[] { a, b } : new object[] { b, a };
        return new BitmapNode((uint)(ba | bb), slots);
    }

    internal sealed class BitmapNode : Node
    {
        public static readonly BitmapNode EmptyNode = new(0, Array.Empty<object>());
        private readonly uint _bitmap;
        private readonly object[] _slots; // Entry or Node

        public BitmapNode(uint bitmap, object[] slots)
        {
            _bitmap = bitmap;
            _slots = slots;
        }

        public static BitmapNode FromSingle(object? replacement, int shift) => replacement switch
        {
            null => EmptyNode,
            BitmapNode b => b,
            Entry e => new BitmapNode((uint)Bit(e.Hash, shift), new object[] { e }),
            CollisionNode c => new BitmapNode((uint)Bit(c.Hash, shift), new object[] { c }),
            _ => throw new InvalidOperationException("unknown HAMT node"),
        };

        private int Index(uint bit) => BitOperations.PopCount(_bitmap & (bit - 1));

        public override bool TryGet(TKey key, int hash, int shift, IEqualityComparer<TKey> cmp, out TValue value)
        {
            var bit = (uint)Bit(hash, shift);
            if ((_bitmap & bit) == 0) { value = default!; return false; }
            switch (_slots[Index(bit)])
            {
                case Entry e when e.Hash == hash && cmp.Equals(e.Key, key):
                    value = e.Value;
                    return true;
                case Node n:
                    return n.TryGet(key, hash, shift + 5, cmp, out value);
                default:
                    value = default!;
                    return false;
            }
        }

        public override Node Set(Entry entry, int shift, IEqualityComparer<TKey> cmp, out bool added)
        {
            var bit = (uint)Bit(entry.Hash, shift);
            var idx = Index(bit);
            if ((_bitmap & bit) == 0)
            {
                added = true;
                var grown = new object[_slots.Length + 1];
                Array.Copy(_slots, 0, grown, 0, idx);
                grown[idx] = entry;
                Array.Copy(_slots, idx, grown, idx + 1, _slots.Length - idx);
                return new BitmapNode(_bitmap | bit, grown);
            }
            object replacement;
            switch (_slots[idx])
            {
                case Entry e when e.Hash == entry.Hash && cmp.Equals(e.Key, entry.Key):
                    added = false;
                    if (EqualityComparer<TValue>.Default.Equals(e.Value, entry.Value)) return this;
                    replacement = entry;
                    break;
                case Entry e:
                    added = true;
                    replacement = Merge(e, entry, shift + 5);
                    break;
                case Node n:
                    var child = n.Set(entry, shift + 5, cmp, out added);
                    if (ReferenceEquals(child, n)) return this;
                    replacement = child;
                    break;
                default:
                    throw new InvalidOperationException("unknown HAMT slot");
            }
            var copy = (object[])_slots.Clone();
            copy[idx] = replacement;
            return new BitmapNode(_bitmap, copy);
        }

        public override object? Remove(TKey key, int hash, int shift, IEqualityComparer<TKey> cmp, out bool removed)
        {
            var bit = (uint)Bit(hash, shift);
            removed = false;
            if ((_bitmap & bit) == 0) return this;
            var idx = Index(bit);
            object? replacement;
            switch (_slots[idx])
            {
                case Entry e when e.Hash == hash && cmp.Equals(e.Key, key):
                    removed = true;
                    replacement = null;
                    break;
                case Node n:
                    replacement = n.Remove(key, hash, shift + 5, cmp, out removed);
                    if (!removed) return this;
                    break;
                default:
                    return this;
            }
            if (replacement is null)
            {
                if (_slots.Length == 1) return null;
                var shrunk = new object[_slots.Length - 1];
                Array.Copy(_slots, 0, shrunk, 0, idx);
                Array.Copy(_slots, idx + 1, shrunk, idx, _slots.Length - idx - 1);
                var node = new BitmapNode(_bitmap & ~bit, shrunk);
                // Canonical shape: a lone entry bubbles up to its parent.
                return shrunk.Length == 1 && shrunk[0] is Entry lone && shift > 0 ? lone : node;
            }
            if (_slots.Length == 1 && replacement is Entry up && shift > 0) return up;
            var copy = (object[])_slots.Clone();
            copy[idx] = replacement;
            return new BitmapNode(_bitmap, copy);
        }

        public override IEnumerable<Entry> Entries()
        {
            foreach (var slot in _slots)
            {
                if (slot is Entry e) yield return e;
                else foreach (var inner in ((Node)slot).Entries()) yield return inner;
            }
        }
    }

    internal sealed class CollisionNode : Node
    {
        public int Hash { get; }
        private readonly Entry[] _entries;

        public CollisionNode(int hash, Entry[] entries)
        {
            Hash = hash;
            _entries = entries;
        }

        private int Find(TKey key, IEqualityComparer<TKey> cmp)
        {
            for (var i = 0; i < _entries.Length; i++)
                if (cmp.Equals(_entries[i].Key, key)) return i;
            return -1;
        }

        public override bool TryGet(TKey key, int hash, int shift, IEqualityComparer<TKey> cmp, out TValue value)
        {
            var i = hash == Hash ? Find(key, cmp) : -1;
            value = i >= 0 ? _entries[i].Value : default!;
            return i >= 0;
        }

        public override Node Set(Entry entry, int shift, IEqualityComparer<TKey> cmp, out bool added)
        {
            if (entry.Hash != Hash)
            {
                // Different hash at this depth: re-split under a bitmap node.
                added = true;
                var asNode = new BitmapNode((uint)Bit(Hash, shift), new object[] { this });
                return asNode.Set(entry, shift, cmp, out added);
            }
            var i = Find(entry.Key, cmp);
            if (i < 0)
            {
                added = true;
                var grown = new Entry[_entries.Length + 1];
                _entries.CopyTo(grown, 0);
                grown[^1] = entry;
                return new CollisionNode(Hash, grown);
            }
            added = false;
            if (EqualityComparer<TValue>.Default.Equals(_entries[i].Value, entry.Value)) return this;
            var copy = (Entry[])_entries.Clone();
            copy[i] = entry;
            return new CollisionNode(Hash, copy);
        }

        public override object? Remove(TKey key, int hash, int shift, IEqualityComparer<TKey> cmp, out bool removed)
        {
            var i = hash == Hash ? Find(key, cmp) : -1;
            removed = i >= 0;
            if (!removed) return this;
            if (_entries.Length == 2) return _entries[1 - i];
            var shrunk = new Entry[_entries.Length - 1];
            Array.Copy(_entries, 0, shrunk, 0, i);
            Array.Copy(_entries, i + 1, shrunk, i, _entries.Length - i - 1);
            return new CollisionNode(Hash, shrunk);
        }

        public override IEnumerable<Entry> Entries() => _entries;
    }
}
