using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ores.Net.Runtime;

public enum ActorTopicDeliveryPolicy
{
    Reliable,
    Latest,
    Lossy
}

public readonly record struct ActorPublishReceipt(
    int Subscribers,
    int Accepted,
    int Dropped,
    int Coalesced,
    bool Backpressured);

/// <summary>
/// NativeAOT-safe, in-process ActorGroup/pub-sub admission substrate.
///
/// This is runtime infrastructure, not source-level Topic&lt;T&gt; lowering.
/// General topic handles cannot impersonate host/supervisor publication.
/// Group membership is cooperation state only and never grants stop/cancel
/// authority. ActorRef's full local capability (ID, generation and nonce) is
/// retained as the incarnation fence.
/// </summary>
public sealed class ActorPubSub
{
    private const int MaxTopicCapacity = 65_536;
    private const int MaxTotalQueueSlots = 1 << 20;
    // Match LLVM's per-delivery maximum; a capacity bound alone cannot
    // bound the memory retained by a single enormous String event.
    private const int MaxFrozenPayloadBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly object _gate = new();
    // Lifetime credits for all topic/subscriber edges in this registry.
    // Every mutation is guarded by _gate, including termination cleanup.
    private int _reservedQueueSlots;
    private readonly int _maxGroups;
    private readonly int _maxGroupsPerActor;
    private readonly int _maxTopics;
    private readonly int _maxSubscriptionsPerActor;

    private readonly Dictionary<ActorRef, ActorRecord> _actors = new();
    private readonly Dictionary<ulong, GroupRecord> _groups = new();
    private readonly Dictionary<TopicKey, ITopicState> _topics = new();
    private ulong _nextGroupId;
    private bool _closed;

    internal ActorPubSub(
        int maxGroups,
        int maxGroupsPerActor,
        int maxTopics,
        int maxSubscriptionsPerActor)
    {
        if (maxGroups <= 0 || maxGroupsPerActor <= 0 ||
            maxTopics <= 0 || maxSubscriptionsPerActor <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxGroups));

        _maxGroups = maxGroups;
        _maxGroupsPerActor = maxGroupsPerActor;
        _maxTopics = maxTopics;
        _maxSubscriptionsPerActor = maxSubscriptionsPerActor;
    }

    internal void RegisterActor(ActorRef actor, ActorKind kind)
    {
        if (actor == default)
            throw new ArgumentException("ActorRef must be live", nameof(actor));
        lock (_gate)
        {
            if (_actors.ContainsKey(actor))
                return;
            _actors.Add(actor, new ActorRecord(kind));
        }
    }

    internal void RetireActor(ActorRef actor)
    {
        lock (_gate)
        {
            if (!_actors.Remove(actor, out ActorRecord? record))
                return;

            foreach (ulong groupId in record.Groups)
            {
                if (_groups.TryGetValue(groupId, out GroupRecord? group))
                    group.Members.Remove(actor);
            }

            foreach (ITopicState topic in _topics.Values)
            {
                if (topic.RemoveActor(actor))
                    _reservedQueueSlots -= topic.Capacity;
            }
        }
    }

    public ActorGroup CreateGroup(int maxMembers = 1024)
    {
        ActorRuntime.RequireHostPubSubAuthority("create an ActorGroup");
        if (maxMembers <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMembers));

        lock (_gate)
        {
            if (_closed)
                throw new ObjectDisposedException(nameof(ActorPubSub));
            if (_groups.Count >= _maxGroups)
                throw new InvalidOperationException("ActorGroup capacity exceeded");
            if (_nextGroupId == ulong.MaxValue)
                throw new OverflowException("ActorGroup ID space exhausted");

            ulong id = ++_nextGroupId;
            var record = new GroupRecord(maxMembers);
            _groups.Add(id, record);
            return new ActorGroup(this, id);
        }
    }

    public sealed class ActorGroup : IDisposable
    {
        private ActorPubSub? _owner;
        internal ActorGroup(ActorPubSub owner, ulong id)
        {
            _owner = owner;
            Id = id;
        }

        public ulong Id { get; }

        private ActorPubSub Owner =>
            _owner ?? throw new ObjectDisposedException(nameof(ActorGroup));

        public bool Join(ActorRef actor)
        {
            ActorRuntime.RequireHostPubSubAuthority("admit an ActorGroup member");
            return Owner.Join(Id, actor);
        }
        public bool Leave(ActorRef actor)
        {
            ActorRuntime.RequireHostPubSubAuthority("revoke an ActorGroup member");
            return Owner.Leave(Id, actor);
        }
        public bool Contains(ActorRef actor) => Owner.Contains(Id, actor);
        public int MemberCount => Owner.MemberCount(Id);

        public ActorTopic<T> DefineTopic<T>(
            string name,
            ActorTopicDeliveryPolicy policy,
            int capacity)
        {
            ActorRuntime.RequireHostPubSubAuthority("define an ActorGroup topic");
            return Owner.DefineTopic<T>(Id, name, policy, capacity);
        }

        public ActorTopic<T> Topic<T>(string name)
            => Owner.Topic<T>(Id, name);

        public void Dispose()
        {
            // Reject before invalidating the caller's handle; an actor cannot
            // hijack group closure and leave the manager with a dead handle.
            ActorRuntime.RequireHostPubSubAuthority("close an ActorGroup");
            ActorPubSub? owner = Interlocked.Exchange(ref _owner, null);
            owner?.CloseGroup(Id);
        }
    }

    public sealed class ActorTopic<T>
    {
        private readonly ActorPubSub _owner;
        internal ActorTopic(ActorPubSub owner, ulong groupId, string name)
        {
            _owner = owner;
            GroupId = groupId;
            Name = name;
        }

        public ulong GroupId { get; }
        public string Name { get; }

        // Host/runtime-only operations: these accept a full ActorRef
        // (including generation and nonce), never an integer actor ID.
        public bool Subscribe(ActorRef actor)
        {
            ActorRuntime.RequireHostPubSubAuthority(
                "subscribe with an unbound ActorRef");
            return _owner.Subscribe<T>(GroupId, Name, actor);
        }

        public bool Unsubscribe(ActorRef actor)
        {
            ActorRuntime.RequireHostPubSubAuthority(
                "unsubscribe with an unbound ActorRef");
            return _owner.Unsubscribe<T>(GroupId, Name, actor);
        }

        public ActorPublishReceipt Publish(ActorRef publisher, T value)
        {
            ActorRuntime.RequireHostPubSubAuthority(
                "publish with an unbound ActorRef");
            return _owner.Publish(GroupId, Name, publisher, value);
        }

        public bool TryRead(ActorRef subscriber, out T value)
        {
            ActorRuntime.RequireHostPubSubAuthority(
                "read with an unbound ActorRef");
            return _owner.TryRead(GroupId, Name, subscriber, out value);
        }

        // Compiler/runtime-only guest bridge. A retained ActorContext is
        // unusable after its physical turn; the compiler never supplies an
        // arbitrary ActorRef as publication or subscription authority.
        // Do not convert these to an AsyncLocal/ExecutionContext capability:
        // logical tasks can suspend and carriers can be reused by other actors.
        public bool Subscribe(ActorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.ValidateActiveTurn();
            return _owner.Subscribe<T>(GroupId, Name, context.Self);
        }

        public bool Unsubscribe(ActorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.ValidateActiveTurn();
            return _owner.Unsubscribe<T>(GroupId, Name, context.Self);
        }

        public ActorPublishReceipt Publish(ActorContext context, T value)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.ValidateActiveTurn();
            return _owner.Publish(GroupId, Name, context.Self, value);
        }

        public bool TryRead(ActorContext context, out T value)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.ValidateActiveTurn();
            return _owner.TryRead(GroupId, Name, context.Self, out value);
        }
    }

    private bool Join(ulong groupId, ActorRef actor)
    {
        lock (_gate)
        {
            ActorRecord actorRecord = RequireActorLocked(actor);
            GroupRecord group = RequireGroupLocked(groupId);

            if (actorRecord.Groups.Contains(groupId))
                return false;
            if (actorRecord.Groups.Count >= _maxGroupsPerActor)
                throw new InvalidOperationException(
                    "Actor cooperation membership quota exceeded");
            if (group.Members.Count >= group.MaxMembers)
                throw new InvalidOperationException("ActorGroup member capacity exceeded");

            group.Members.Add(actor);
            actorRecord.Groups.Add(groupId);
            return true;
        }
    }

    private bool Leave(ulong groupId, ActorRef actor)
    {
        lock (_gate)
        {
            if (!_actors.TryGetValue(actor, out ActorRecord? actorRecord) ||
                !_groups.TryGetValue(groupId, out GroupRecord? group))
                return false;
            if (!actorRecord.Groups.Remove(groupId))
                return false;

            group.Members.Remove(actor);
            foreach (var entry in _topics)
            {
                if (entry.Key.GroupId != groupId ||
                    !entry.Value.RemoveActor(actor)) continue;
                // An edge always owns one queue-slot reservation even if its
                // actor accounting was already found inconsistent.
                _reservedQueueSlots -= entry.Value.Capacity;
                if (actorRecord.SubscriptionCount > 0)
                    actorRecord.SubscriptionCount--;
            }
            return true;
        }
    }

    private bool Contains(ulong groupId, ActorRef actor)
    {
        lock (_gate)
            return _actors.ContainsKey(actor) &&
                _groups.TryGetValue(groupId, out GroupRecord? group) &&
                group.Members.Contains(actor);
    }

    private int MemberCount(ulong groupId)
    {
        lock (_gate)
            return RequireGroupLocked(groupId).Members.Count;
    }

    private ActorTopic<T> DefineTopic<T>(
        ulong groupId,
        string name,
        ActorTopicDeliveryPolicy policy,
        int capacity)
    {
        ValidateTopicName(name);
        ValidatePayloadType<T>();
        if (capacity <= 0 || capacity > MaxTopicCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "Actor topic capacity exceeds the bounded ring limit");
        if (policy == ActorTopicDeliveryPolicy.Latest && capacity != 1)
            throw new ArgumentException("LATEST topic capacity must equal one", nameof(capacity));

        lock (_gate)
        {
            _ = RequireGroupLocked(groupId);
            var key = new TopicKey(groupId, name);
            if (_topics.TryGetValue(key, out ITopicState? existing))
            {
                if (existing is not TopicState<T> typed ||
                    typed.Policy != policy || typed.Capacity != capacity)
                    throw new InvalidOperationException(
                        "Topic already exists with different type/policy/capacity");
                return new ActorTopic<T>(this, groupId, name);
            }

            if (_topics.Count >= _maxTopics)
                throw new InvalidOperationException("Topic capacity exceeded");

            _topics.Add(key, new TopicState<T>(policy, capacity));
            return new ActorTopic<T>(this, groupId, name);
        }
    }

    private ActorTopic<T> Topic<T>(ulong groupId, string name)
    {
        ValidateTopicName(name);
        lock (_gate)
        {
            _ = RequireTopicLocked<T>(groupId, name);
            return new ActorTopic<T>(this, groupId, name);
        }
    }

    private bool Subscribe<T>(ulong groupId, string name, ActorRef actor)
    {
        lock (_gate)
        {
            ActorRecord actorRecord = RequireActorLocked(actor);
            GroupRecord group = RequireGroupLocked(groupId);
            if (!group.Members.Contains(actor))
                throw new InvalidOperationException(
                    "Subscription requires live ActorGroup membership");
            if (actorRecord.Kind != ActorKind.Shared)
                throw new InvalidOperationException(
                    "Zero-copy in-memory topic subscription requires a shared actor");

            TopicState<T> topic = RequireTopicLocked<T>(groupId, name);
            if (topic.Contains(actor))
                return false;
            if (actorRecord.SubscriptionCount >= _maxSubscriptionsPerActor)
                throw new InvalidOperationException(
                    "Actor topic subscription quota exceeded");
            if (_reservedQueueSlots > MaxTotalQueueSlots - topic.Capacity)
                throw new InvalidOperationException(
                    "Actor topic aggregate queue-slot quota exceeded");

            // All fixed storage is allocated before publishing the edge.
            // Throwing here must not consume a logical reservation.
            topic.Add(actor);
            _reservedQueueSlots += topic.Capacity;
            actorRecord.SubscriptionCount++;
            return true;
        }
    }

    private bool Unsubscribe<T>(ulong groupId, string name, ActorRef actor)
    {
        lock (_gate)
        {
            TopicState<T> topic = RequireTopicLocked<T>(groupId, name);
            if (!topic.RemoveActor(actor))
                return false;
            _reservedQueueSlots -= topic.Capacity;
            if (_actors.TryGetValue(actor, out ActorRecord? actorRecord) &&
                actorRecord.SubscriptionCount > 0)
                actorRecord.SubscriptionCount--;
            return true;
        }
    }

    private ActorPublishReceipt Publish<T>(
        ulong groupId,
        string name,
        ActorRef publisher,
        T value)
    {
        lock (_gate)
        {
            _ = RequireActorLocked(publisher);
            GroupRecord group = RequireGroupLocked(groupId);
            if (!group.Members.Contains(publisher))
                throw new InvalidOperationException(
                    "Publication requires live ActorGroup membership");

            TopicState<T> topic = RequireTopicLocked<T>(groupId, name);
            PruneStaleLocked(groupId, topic);
            return topic.Publish(Freeze(value));
        }
    }

    private bool TryRead<T>(
        ulong groupId,
        string name,
        ActorRef subscriber,
        out T value)
    {
        lock (_gate)
        {
            _ = RequireActorLocked(subscriber);
            GroupRecord group = RequireGroupLocked(groupId);
            if (!group.Members.Contains(subscriber))
                throw new InvalidOperationException(
                    "Topic read requires live ActorGroup membership");

            TopicState<T> topic = RequireTopicLocked<T>(groupId, name);
            return topic.TryRead(subscriber, out value);
        }
    }

    private void CloseGroup(ulong groupId)
    {
        lock (_gate)
        {
            if (!_groups.Remove(groupId, out GroupRecord? group))
                return;

            foreach (ActorRef member in group.Members)
            {
                if (_actors.TryGetValue(member, out ActorRecord? actor))
                    actor.Groups.Remove(groupId);
            }

            var remove = _topics.Keys.Where(k => k.GroupId == groupId).ToArray();
            foreach (TopicKey key in remove)
            {
                ITopicState topic = _topics[key];
                foreach (ActorRef actor in topic.Subscribers)
                {
                    _reservedQueueSlots -= topic.Capacity;
                    if (_actors.TryGetValue(actor, out ActorRecord? record) &&
                        record.SubscriptionCount > 0)
                        record.SubscriptionCount--;
                }
                _topics.Remove(key);
            }
        }
    }

    private void PruneStaleLocked<T>(ulong groupId, TopicState<T> topic)
    {
        ActorRef[] stale = topic.Subscribers
            .Where(actor =>
                !_actors.ContainsKey(actor) ||
                !_groups.TryGetValue(groupId, out GroupRecord? group) ||
                !group.Members.Contains(actor))
            .ToArray();

        foreach (ActorRef actor in stale)
        {
            if (!topic.RemoveActor(actor)) continue;
            _reservedQueueSlots -= topic.Capacity;
            if (_actors.TryGetValue(actor, out ActorRecord? record) &&
                record.SubscriptionCount > 0)
                record.SubscriptionCount--;
        }
    }

    private ActorRecord RequireActorLocked(ActorRef actor)
    {
        if (!_actors.TryGetValue(actor, out ActorRecord? record))
            throw new InvalidOperationException("Unknown or stale ActorRef");
        return record;
    }

    private GroupRecord RequireGroupLocked(ulong groupId)
    {
        if (!_groups.TryGetValue(groupId, out GroupRecord? group))
            throw new ObjectDisposedException(nameof(ActorGroup));
        return group;
    }

    private TopicState<T> RequireTopicLocked<T>(ulong groupId, string name)
    {
        if (!_topics.TryGetValue(new TopicKey(groupId, name), out ITopicState? raw) ||
            raw is not TopicState<T> typed)
            throw new InvalidOperationException("Unknown topic or payload type mismatch");
        return typed;
    }

    private static void ValidateTopicName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 || name.Any(char.IsControl))
            throw new ArgumentException("Invalid topic name", nameof(name));
    }

    private static void ValidatePayloadType<T>()
    {
        Type type = typeof(T);
        if (type == typeof(string) ||
            type == typeof(bool) || type == typeof(byte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong) ||
            type == typeof(float) || type == typeof(double) ||
            type == typeof(decimal) || type == typeof(Guid))
            return;

        throw new NotSupportedException(
            "NativeAOT pub/sub payload type is not yet a proven frozen scalar/string value");
    }

    private static T Freeze<T>(T value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value),
                "Oreslang event values use Option<T>, not null");

        // Reject malformed UTF-16 rather than letting .NET's default
        // replacement fallback change the value transported by LLVM's
        // strict UTF-8 String ABI. UTF-8 size, not UTF-16 Length, governs
        // the immutable payload limit.
        if (value is string text)
        {
            int byteLength;
            try
            {
                byteLength = StrictUtf8.GetByteCount(text);
            }
            catch (EncoderFallbackException error)
            {
                throw new ArgumentException(
                    "ActorGroup String contains invalid UTF-16", nameof(value), error);
            }
            if (byteLength > MaxFrozenPayloadBytes)
                throw new InvalidOperationException(
                    "ActorGroup event exceeds 16 MiB per-delivery payload limit");
        }
        return value;
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _topics.Clear();
            _groups.Clear();
            _actors.Clear();
            _reservedQueueSlots = 0;
        }
    }

    private sealed class ActorRecord
    {
        internal ActorRecord(ActorKind kind) => Kind = kind;
        internal ActorKind Kind { get; }
        internal HashSet<ulong> Groups { get; } = new();
        internal int SubscriptionCount;
    }

    private sealed class GroupRecord
    {
        internal GroupRecord(int maxMembers) => MaxMembers = maxMembers;
        internal int MaxMembers { get; }
        internal HashSet<ActorRef> Members { get; } = new();
    }

    private readonly record struct TopicKey(ulong GroupId, string Name);

    private interface ITopicState
    {
        IEnumerable<ActorRef> Subscribers { get; }
        int Capacity { get; }
        bool RemoveActor(ActorRef actor);
    }

    private sealed class TopicState<T> : ITopicState
    {
        private readonly Dictionary<ActorRef, Queue<T>> _subscribers = new();

        internal TopicState(ActorTopicDeliveryPolicy policy, int capacity)
        {
            Policy = policy;
            Capacity = capacity;
        }

        internal ActorTopicDeliveryPolicy Policy { get; }
        public int Capacity { get; }
        public IEnumerable<ActorRef> Subscribers => _subscribers.Keys;

        internal bool Contains(ActorRef actor) => _subscribers.ContainsKey(actor);
        // Preallocate every bounded ring *before* publishing the subscription.
        // A RELIABLE fanout checks capacity for all edges before enqueue.
        // Enqueue must then have no allocation/resize failure path that could
        // commit to an early subscriber and fail at a later one.
        internal void Add(ActorRef actor) =>
            _subscribers.Add(actor, new Queue<T>(Capacity));
        public bool RemoveActor(ActorRef actor) => _subscribers.Remove(actor);

        internal ActorPublishReceipt Publish(T value)
        {
            if (Policy == ActorTopicDeliveryPolicy.Reliable)
            {
                foreach (Queue<T> queue in _subscribers.Values)
                {
                    if (queue.Count >= Capacity)
                        return new ActorPublishReceipt(
                            _subscribers.Count, 0, 0, 0, Backpressured: true);
                }

                foreach (Queue<T> queue in _subscribers.Values)
                    queue.Enqueue(value);

                return new ActorPublishReceipt(
                    _subscribers.Count, _subscribers.Count, 0, 0, Backpressured: false);
            }

            int accepted = 0;
            int dropped = 0;
            int coalesced = 0;
            foreach (Queue<T> queue in _subscribers.Values)
            {
                if (Policy == ActorTopicDeliveryPolicy.Latest)
                {
                    if (queue.Count != 0)
                    {
                        queue.Dequeue();
                        coalesced++;
                    }
                    queue.Enqueue(value);
                    accepted++;
                }
                else if (queue.Count >= Capacity)
                {
                    dropped++;
                }
                else
                {
                    queue.Enqueue(value);
                    accepted++;
                }
            }

            return new ActorPublishReceipt(
                _subscribers.Count, accepted, dropped, coalesced, Backpressured: false);
        }

        internal bool TryRead(ActorRef actor, out T value)
        {
            if (!_subscribers.TryGetValue(actor, out Queue<T>? queue))
                throw new InvalidOperationException("Actor is not subscribed to topic");
            if (queue.Count == 0)
            {
                value = default!;
                return false;
            }
            value = queue.Dequeue();
            return true;
        }
    }
}
