using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ores.Net.Runtime;

/// <summary>
/// Host-only immutable actor map. Path-copy AVL nodes give deterministic
/// O(log n) update/lookup without adopting mutable containers.
/// Source-language persistent collections remain a separate compiler gate.
/// </summary>
public sealed class PersistentActorMap
{
    private sealed class Node
    {
        internal readonly string Key;
        internal readonly object Value;
        internal readonly Node? Left;
        internal readonly Node? Right;
        internal readonly int Height;

        internal Node(string key, object value, Node? left, Node? right)
        {
            Key = key;
            Value = value;
            Left = left;
            Right = right;
            Height = 1 + Math.Max(HeightOf(left), HeightOf(right));
        }
    }

    private readonly Node? _root;
    public int Count { get; }
    public static PersistentActorMap Empty { get; } = new(null, 0);

    private PersistentActorMap(Node? root, int count)
    {
        _root = root;
        Count = count;
    }

    public bool TryGetValue(string key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        Node? node = _root;
        while (node is not null)
        {
            int order = StringComparer.Ordinal.Compare(key, node.Key);
            if (order == 0)
            {
                value = node.Value;
                return true;
            }
            node = order < 0 ? node.Left : node.Right;
        }
        value = null;
        return false;
    }

    public object? Get(string key) => TryGetValue(key, out var value) ? value : null;

    public PersistentActorMap SnapshotForActor(ActorDataIsolation isolation) =>
        isolation switch
        {
            ActorDataIsolation.Shared => this,
            ActorDataIsolation.Private or ActorDataIsolation.Untrusted =>
                CloneIsolated(0),
            _ => throw new ArgumentOutOfRangeException(nameof(isolation))
        };

    private PersistentActorMap CloneIsolated(int depth)
    {
        var memo = new Dictionary<PersistentActorMap, PersistentActorMap>(
            ReferenceEqualityComparer.Instance);
        int graphNodes = 0;
        return CloneIsolated(depth, memo, ref graphNodes);
    }

    private PersistentActorMap CloneIsolated(
        int depth,
        Dictionary<PersistentActorMap, PersistentActorMap> memo,
        ref int graphNodes)
    {
        if (depth > 256)
            throw new InvalidOperationException("persistent actor clone depth");
        if (memo.TryGetValue(this, out var prior))
            return prior;
        int count = 0;
        Node? root = CloneNode(_root, depth, memo, ref graphNodes, ref count);
        PersistentActorMap copy = root is null ? Empty : new PersistentActorMap(root, count);
        memo[this] = copy;
        return copy;
    }

    private static Node? CloneNode(
        Node? node,
        int depth,
        Dictionary<PersistentActorMap, PersistentActorMap> memo,
        ref int graphNodes,
        ref int count)
    {
        if (node is null) return null;
        if (++graphNodes > 100000)
            throw new InvalidOperationException("persistent actor clone node budget");
        object value = node.Value is PersistentActorMap nested
            ? nested.CloneIsolated(depth + 1, memo, ref graphNodes)
            : node.Value;
        Node? left = CloneNode(node.Left, depth, memo, ref graphNodes, ref count);
        Node? right = CloneNode(node.Right, depth, memo, ref graphNodes, ref count);
        count++;
        return new Node(node.Key, value, left, right);
    }

    public PersistentActorMap With(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 || key.Length > 4096)
            throw new ArgumentOutOfRangeException(nameof(key), "persistent actor key limit");
        RequireShareableValue(value);
        bool added = false;
        bool changed = false;
        Node next = Put(_root, key, value, ref added, ref changed);
        if (!changed) return this;
        return new PersistentActorMap(next, checked(Count + (added ? 1 : 0)));
    }

    public PersistentActorMap Without(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0) throw new ArgumentOutOfRangeException(nameof(key));
        bool removed = false;
        Node? next = Delete(_root, key, ref removed);
        if (!removed) return this;
        return next is null ? Empty : new PersistentActorMap(next, Count - 1);
    }

    private static void RequireShareableValue(object? value)
    {
        if (value is null)
            throw new ArgumentException("persistent actor values cannot be null");
        Type type = value.GetType();
        if (type == typeof(float))
        {
            if (float.IsFinite((float)value)) return;
            throw new ArgumentException("persistent actor float must be finite");
        }
        if (type == typeof(double))
        {
            if (double.IsFinite((double)value)) return;
            throw new ArgumentException("persistent actor double must be finite");
        }
        if (type == typeof(string) || type == typeof(bool) || type == typeof(char) ||
            type == typeof(byte) || type == typeof(short) || type == typeof(int) ||
            type == typeof(long) || type == typeof(decimal) ||
            type == typeof(BigInteger) || type == typeof(PersistentActorMap))
            return;
        throw new ArgumentException(
            "persistent actor value must be a closed immutable scalar or PersistentActorMap");
    }

    private static bool SameUpdateValue(object left, object right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is PersistentActorMap || right is PersistentActorMap) return false;
        return left.Equals(right);
    }

    private static int HeightOf(Node? node) => node?.Height ?? 0;
    private static int BalanceFactor(Node node) => HeightOf(node.Left) - HeightOf(node.Right);

    private static Node Put(Node? node, string key, object value,
                            ref bool added, ref bool changed)
    {
        if (node is null)
        {
            added = changed = true;
            return new Node(key, value, null, null);
        }
        int order = StringComparer.Ordinal.Compare(key, node.Key);
        if (order == 0)
        {
            if (SameUpdateValue(node.Value, value)) return node;
            changed = true;
            return new Node(node.Key, value, node.Left, node.Right);
        }
        if (order < 0)
        {
            Node left = Put(node.Left, key, value, ref added, ref changed);
            if (!changed) return node;
            return Balance(new Node(node.Key, node.Value, left, node.Right));
        }
        Node right = Put(node.Right, key, value, ref added, ref changed);
        if (!changed) return node;
        return Balance(new Node(node.Key, node.Value, node.Left, right));
    }

    private static Node? Delete(Node? node, string key, ref bool removed)
    {
        if (node is null) return null;
        int order = StringComparer.Ordinal.Compare(key, node.Key);
        if (order < 0)
        {
            Node? left = Delete(node.Left, key, ref removed);
            if (!removed) return node;
            return Balance(new Node(node.Key, node.Value, left, node.Right));
        }
        if (order > 0)
        {
            Node? right = Delete(node.Right, key, ref removed);
            if (!removed) return node;
            return Balance(new Node(node.Key, node.Value, node.Left, right));
        }

        removed = true;
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        Node successor = Min(node.Right);
        Node? replacementRight = DeleteMin(node.Right);
        return Balance(new Node(successor.Key, successor.Value, node.Left, replacementRight));
    }

    private static Node Min(Node node)
    {
        while (node.Left is not null) node = node.Left;
        return node;
    }

    private static Node? DeleteMin(Node node)
    {
        if (node.Left is null) return node.Right;
        return Balance(new Node(node.Key, node.Value, DeleteMin(node.Left), node.Right));
    }

    private static Node Balance(Node node)
    {
        int factor = BalanceFactor(node);
        if (factor > 1)
        {
            if (node.Left is null) throw new InvalidOperationException("AVL left invariant");
            Node left = node.Left;
            if (BalanceFactor(left) < 0)
                left = RotateLeft(left);
            return RotateRight(new Node(node.Key, node.Value, left, node.Right));
        }
        if (factor < -1)
        {
            if (node.Right is null) throw new InvalidOperationException("AVL right invariant");
            Node right = node.Right;
            if (BalanceFactor(right) > 0)
                right = RotateRight(right);
            return RotateLeft(new Node(node.Key, node.Value, node.Left, right));
        }
        return node;
    }

    private static Node RotateLeft(Node node)
    {
        Node pivot = node.Right ?? throw new InvalidOperationException("AVL rotate-left");
        Node moved = new(node.Key, node.Value, node.Left, pivot.Left);
        return new Node(pivot.Key, pivot.Value, moved, pivot.Right);
    }

    private static Node RotateRight(Node node)
    {
        Node pivot = node.Left ?? throw new InvalidOperationException("AVL rotate-right");
        Node moved = new(node.Key, node.Value, pivot.Right, node.Right);
        return new Node(pivot.Key, pivot.Value, pivot.Left, moved);
    }
}

public enum ActorDataIsolation
{
    Shared,
    Private,
    Untrusted
}

/// <summary>
/// Bounded port specialized to immutable persistent actor roots. Receiving does
/// not refund credits; only explicit receiver acknowledgement does.
/// </summary>
public sealed class PersistentActorMapPort
{
    private sealed record Envelope(long Id, long Bytes, PersistentActorMap State);
    private readonly object _gate = new();
    private readonly Queue<Envelope> _queue = new();
    private readonly ActorDataIsolation _isolation;
    private readonly int _maxFrames;
    private readonly long _maxBytes;
    private int _usedFrames;
    private long _usedBytes;
    private long _nextId = 1;
    private bool _closed;

    public PersistentActorMapPort(
        ActorDataIsolation isolation, int maxFrames, long maxBytes)
    {
        if (!Enum.IsDefined(typeof(ActorDataIsolation), isolation))
            throw new ArgumentOutOfRangeException(nameof(isolation));
        if (maxFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _isolation = isolation;
        _maxFrames = maxFrames;
        _maxBytes = maxBytes;
    }

    public bool Offer(PersistentActorMap state, long chargeBytes)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (chargeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chargeBytes));
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("persistent actor port closed");
            if (_nextId == long.MaxValue)
                throw new OverflowException("persistent actor delivery identity exhausted");
            if (_usedFrames >= _maxFrames || chargeBytes > _maxBytes - _usedBytes)
                return false; // sender retains immutable root and ownership
            // Snapshot after quota admission but before publishing any credit.
            // A failed isolated copy leaves queue/counters untouched.
            PersistentActorMap admitted = state.SnapshotForActor(_isolation);
            _queue.Enqueue(new Envelope(_nextId++, chargeBytes, admitted));
            _usedFrames++;
            _usedBytes += chargeBytes;
            return true;
        }
    }

    public Delivery? TryReceive()
    {
        lock (_gate)
        {
            if (_queue.Count == 0) return null;
            return new Delivery(this, _queue.Dequeue());
        }
    }

    public (int Frames, long Bytes) Used
    {
        get { lock (_gate) return (_usedFrames, _usedBytes); }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            if (_usedFrames != 0)
                throw new InvalidOperationException("persistent actor port has unacknowledged deliveries");
        }
    }

    public sealed class Delivery
    {
        private PersistentActorMapPort? _owner;
        private readonly long _bytes;
        public long Id { get; }
        public PersistentActorMap State { get; }

        private Delivery(PersistentActorMapPort owner, Envelope envelope)
        {
            _owner = owner;
            Id = envelope.Id;
            _bytes = envelope.Bytes;
            State = envelope.State;
        }

        public bool Acknowledge()
        {
            var owner = _owner;
            if (owner is null) return false;
            lock (owner._gate)
            {
                if (!ReferenceEquals(_owner, owner)) return false;
                if (owner._usedFrames <= 0 || owner._usedBytes < _bytes)
                    throw new InvalidOperationException("persistent actor port accounting corrupted");
                owner._usedFrames--;
                owner._usedBytes -= _bytes;
                _owner = null;
                return true;
            }
        }
    }
}
