namespace Ores.Net.Runtime;

/// <summary>
/// Host-only, actor-incarnation-local checked compressed32 allocation authority.
/// This is NOT an OS sandbox or separate CLR heap. Guest lowering must make
/// these checks unavoidable before native dereferences; host bases stay private.
/// </summary>
public sealed class ActorIsolationAccessV1
{
    public const ulong MaxCompressed32Span = (ulong)uint.MaxValue * 8UL;

    public sealed class Grant
    {
        internal Grant(object issuer, ulong id) { Issuer = issuer; Id = id; }
        internal object Issuer { get; }
        internal ulong Id { get; }
    }

    private readonly object _gate = new();
    private readonly object _issuer = new();
    private readonly ulong _span;
    private readonly Dictionary<ulong, (ulong Start, ulong End)> _live = new();
    private ulong _nextId = 1;
    private bool _retired;

    public ActorIsolationAccessV1(ulong tenant, ulong actor, ulong generation, ulong span)
    {
        if (tenant == 0 || actor == 0 || generation == 0 ||
            span == 0 || span > MaxCompressed32Span)
            throw new ArgumentOutOfRangeException(nameof(span),
                "invalid tenant/actor incarnation or compressed32 span");
        _span = span;
    }

    public Grant IssueAllocation(ulong start, ulong length)
    {
        lock (_gate)
        {
            if (_retired) throw new InvalidOperationException("retired actor");
            if (start % 8 != 0 || length == 0 || start > _span ||
                length > _span - start || _nextId == ulong.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(length));
            ulong end = start + length; // safe after subtract-first check
            foreach (var range in _live.Values)
                if (start < range.End && range.Start < end)
                    throw new InvalidOperationException("overlapping live allocations");
            var grant = new Grant(_issuer, _nextId++);
            _live.Add(grant.Id, (start, end));
            return grant;
        }
    }

    /// <summary>Return actor-relative byte offset, never a native pointer.</summary>
    public bool TryCheckedOffset(Grant? grant, uint word, ulong bytes, out ulong offset)
    {
        lock (_gate)
        {
            offset = 0;
            if (_retired || grant is null ||
                !ReferenceEquals(grant.Issuer, _issuer) ||
                !_live.TryGetValue(grant.Id, out var range) || word == 0 || bytes == 0)
                return false;
            ulong at = ((ulong)word - 1UL) * 8UL;
            if (at < range.Start || at > range.End || bytes > range.End - at)
                return false;
            offset = at;
            return true;
        }
    }

    public bool Release(Grant? grant)
    {
        lock (_gate)
        {
            if (_retired || grant is null || !ReferenceEquals(grant.Issuer, _issuer))
                return false;
            // Owning allocator MUST scrub before reusing the underlying bytes.
            return _live.Remove(grant.Id);
        }
    }

    public void Retire()
    {
        lock (_gate)
        {
            _retired = true;
            _live.Clear();
            // Supervisor MUST quiesce turns/FFI pins and scrub the arena.
        }
    }
}
