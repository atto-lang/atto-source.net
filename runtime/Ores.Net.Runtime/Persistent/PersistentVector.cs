// Persistent immutable vector: 32-way radix trie with a 32-element tail.
// Append, SetItem and RemoveLast copy only the O(log32 n) path they touch;
// every previous version stays valid and unchanged. Indexing is
// O(log32 n), at most 7 levels for int.MaxValue elements. Leaves are T[]
// so value types are not boxed. No reflection or locking: NativeAOT- and
// actor-safe.
using System.Collections;

namespace Ores.Net.Runtime.Persistent;

public sealed class PersistentVector<T> : IReadOnlyList<T>
{
    private const int Bits = 5;
    private const int Width = 1 << Bits;
    private const int Mask = Width - 1;

    private readonly int _shift;
    private readonly object[] _root; // children: object[] (internal) or T[] (leaf)
    private readonly T[] _tail;

    public int Count { get; }

    public static PersistentVector<T> Empty { get; } = new(0, Bits, new object[Width], Array.Empty<T>());

    private PersistentVector(int count, int shift, object[] root, T[] tail)
    {
        Count = count;
        _shift = shift;
        _root = root;
        _tail = tail;
    }

    public static PersistentVector<T> From(IEnumerable<T> items)
    {
        var v = Empty;
        foreach (var item in items) v = v.Add(item);
        return v;
    }

    private int TailOffset => Count < Width ? 0 : ((Count - 1) >>> Bits) << Bits;

    private T[] LeafFor(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index >= TailOffset) return _tail;
        object node = _root;
        for (var level = _shift; level > 0; level -= Bits)
            node = ((object[])node)[(index >>> level) & Mask];
        return (T[])node;
    }

    public T this[int index] => LeafFor(index)[index & Mask];

    public PersistentVector<T> Add(T value)
    {
        if (Count == int.MaxValue) throw new InvalidOperationException("vector length limit");
        if (Count - TailOffset < Width)
        {
            var tail = new T[_tail.Length + 1];
            _tail.CopyTo(tail, 0);
            tail[^1] = value;
            return new(Count + 1, _shift, _root, tail);
        }
        object[] root;
        var shift = _shift;
        if ((Count >>> Bits) > (1 << _shift))
        {
            root = new object[Width];
            root[0] = _root;
            root[1] = NewPath(_shift, _tail);
            shift += Bits;
        }
        else
        {
            root = PushTail(_shift, _root, _tail);
        }
        return new(Count + 1, shift, root, new[] { value });
    }

    private object[] PushTail(int level, object[] parent, T[] tailNode)
    {
        var copy = (object[])parent.Clone();
        var sub = ((Count - 1) >>> level) & Mask;
        object insert = level == Bits
            ? tailNode
            : parent[sub] is object[] child
                ? PushTail(level - Bits, child, tailNode)
                : NewPath(level - Bits, tailNode);
        copy[sub] = insert;
        return copy;
    }

    private static object NewPath(int level, T[] leaf)
    {
        if (level == 0) return leaf;
        var node = new object[Width];
        node[0] = NewPath(level - Bits, leaf);
        return node;
    }

    public PersistentVector<T> SetItem(int index, T value)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index >= TailOffset)
        {
            var tail = (T[])_tail.Clone();
            tail[index & Mask] = value;
            return new(Count, _shift, _root, tail);
        }
        return new(Count, _shift, (object[])Assoc(_shift, _root, index, value), _tail);
    }

    private static object Assoc(int level, object node, int index, T value)
    {
        if (level == 0)
        {
            var leaf = (T[])((T[])node).Clone();
            leaf[index & Mask] = value;
            return leaf;
        }
        var copy = (object[])((object[])node).Clone();
        var sub = (index >>> level) & Mask;
        copy[sub] = Assoc(level - Bits, copy[sub], index, value);
        return copy;
    }

    public PersistentVector<T> RemoveLast()
    {
        if (Count == 0) throw new InvalidOperationException("vector is empty");
        if (Count == 1) return Empty;
        if (Count - TailOffset > 1)
        {
            var tail = new T[_tail.Length - 1];
            Array.Copy(_tail, tail, tail.Length);
            return new(Count - 1, _shift, _root, tail);
        }
        var newTail = LeafFor(Count - 2);
        var root = PopTail(_shift, _root) ?? new object[Width];
        var shift = _shift;
        if (shift > Bits && root[1] is null)
        {
            root = (object[])root[0];
            shift -= Bits;
        }
        return new(Count - 1, shift, root, newTail);
    }

    private object[]? PopTail(int level, object[] node)
    {
        var sub = ((Count - 2) >>> level) & Mask;
        if (level > Bits)
        {
            var child = PopTail(level - Bits, (object[])node[sub]);
            if (child is null && sub == 0) return null;
            var copy = (object[])node.Clone();
            copy[sub] = child!;
            return copy;
        }
        if (sub == 0) return null;
        var leafCopy = (object[])node.Clone();
        leafCopy[sub] = null!;
        return leafCopy;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i += Width)
        {
            var leaf = LeafFor(i);
            var n = Math.Min(Width, Count - i);
            for (var j = 0; j < n; j++) yield return leaf[j];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
