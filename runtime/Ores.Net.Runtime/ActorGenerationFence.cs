namespace Ores.Net.Runtime;

/// <summary>
/// Actor-generation lease used to fence compiler-lowered continuations.
/// Lock order: select arbiter -> generation fence -> one readiness source.
/// </summary>
public sealed class ActorGenerationFence
{
    private readonly object _gate = new();
    private ulong _generation = 1;
    private bool _commitInProgress;

    public ulong Generation
    {
        get { lock (_gate) return _generation; }
    }

    public Lease Capture()
    {
        lock (_gate)
            return new Lease(this, _generation);
    }

    public ulong AdvanceGeneration()
    {
        lock (_gate)
        {
            if (_commitInProgress)
                throw new InvalidOperationException(
                    "Actor generation cannot advance inside a fenced commit");
            if (_generation == ulong.MaxValue)
                throw new OverflowException("Actor generation exhausted");
            return ++_generation;
        }
    }

    private bool IsCurrent(ulong generation)
    {
        lock (_gate)
            return _generation == generation;
    }

    private bool TryRun(ulong generation, Func<bool> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (_gate)
        {
            if (_generation != generation) return false;
            if (_commitInProgress)
                throw new InvalidOperationException(
                    "Generation commit must not re-enter its fence");

            _commitInProgress = true;
            try
            {
                return commit();
            }
            finally
            {
                _commitInProgress = false;
            }
        }
    }

    public sealed class Lease
    {
        private readonly ActorGenerationFence _owner;
        private readonly ulong _generation;

        internal Lease(ActorGenerationFence owner, ulong generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public ulong Generation => _generation;
        public bool IsCurrent => _owner.IsCurrent(_generation);
        public bool TryRun(Func<bool> commit) =>
            _owner.TryRun(_generation, commit);
    }
}
