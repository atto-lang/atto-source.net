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
        if (type == typeof(string) || type == typeof(bool) || type == typeof(char) ||
            type == typeof(byte) || type == typeof(short) || type == typeof(int) ||
            type == typeof(long) || type == typeof(float) || type == typeof(double) ||
            type == typeof(decimal) || type == typeof(BigInteger) ||
            type == typeof(PersistentActorMap))
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

/// <summary>
/// Bounded port specialized to immutable persistent actor roots. Receiving does
/// not refund credits; only explicit receiver acknowledgement does.
/// </summary>
public sealed class PersistentActorMapPort
{
    private sealed record Envelope(long Id, long Bytes, PersistentActorMap State);
    private readonly object _gate = new();
    private readonly Queue<Envelope> _queue = new();
    private readonly int _maxFrames;
    private readonly long _maxBytes;
    private int _usedFrames;
    private long _usedBytes;
    private long _nextId = 1;
    private bool _closed;

    public PersistentActorMapPort(int maxFrames, long maxBytes)
    {
        if (maxFrames <= 0 || maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrames));
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
            _queue.Enqueue(new Envelope(_nextId++, chargeBytes, state));
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
