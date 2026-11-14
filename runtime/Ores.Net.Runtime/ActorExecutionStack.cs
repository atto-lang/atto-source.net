namespace Ores.Net.Runtime;

/// <summary>
/// One logical stack owned exclusively by one execution context. Its actor's
/// physical frame storage may be reused only after this stack completes.
/// This synchronous implementation cannot detach or resume an execution;
/// a future continuation must retain this object and its execution identity.
/// </summary>
internal sealed class ActorExecutionStack
{
    private readonly ActorFrameStorage _storage;
    private readonly ActorExecutionContext _execution;
    private bool _active;
    private bool _completed;

    internal ActorExecutionStack(ActorFrameStorage storage, ActorExecutionContext execution)
    {
        _storage = storage;
        _execution = execution;
    }

    internal void Begin(ActorArenaTurn turn)
    {
        if (_active || _completed) throw new InvalidOperationException("Execution stack already started");
        _storage.BeginExecution(turn, _execution);
        _active = true;
    }

    private void Validate(ActorArenaTurn turn)
    {
        if (!_active || _completed) throw new InvalidOperationException("Execution stack is not active");
        _storage.ValidateExecution(turn, _execution);
    }

    internal void Complete(ActorArenaTurn turn)
    {
        Validate(turn);
        _storage.EndExecution(turn, _execution);
        _active = false;
        _completed = true;
    }

    internal ActorStackFrameHandle Push(ActorArenaTurn turn, int bytes, int alignment)
    {
        Validate(turn);
        return _storage.Push(turn, bytes, alignment);
    }

    internal void Read(ActorArenaTurn turn, ActorStackFrameHandle frame, int offset, Span<byte> bytes)
    { Validate(turn); _storage.Read(turn, frame, offset, bytes); }

    internal void Write(ActorArenaTurn turn, ActorStackFrameHandle frame, int offset, ReadOnlySpan<byte> bytes)
    { Validate(turn); _storage.Write(turn, frame, offset, bytes); }

    internal void Pop(ActorArenaTurn turn, ActorStackFrameHandle frame)
    { Validate(turn); _storage.Pop(turn, frame); }

    internal void SealScalarFrame(ActorArenaTurn turn, ActorStackFrameHandle frame,
        ReadOnlySpan<byte> declaredTypes, int blockCount)
    { Validate(turn); _storage.SealScalarFrame(turn, frame, declaredTypes, blockCount); }

    internal void ValidateScalarHeader(ActorArenaTurn turn, ActorStackFrameHandle frame, int slots, int blocks)
    { Validate(turn); _storage.ValidateScalarHeader(turn, frame, slots, blocks); }

    internal void ValidateScalarSlot(ActorArenaTurn turn, ActorStackFrameHandle frame, int slot, byte kind)
    { Validate(turn); _storage.ValidateScalarSlot(turn, frame, slot, kind); }

    internal void ValidateSynchronousTurnExit(ActorArenaTurn turn)
    { Validate(turn); _storage.ValidateSynchronousTurnExit(turn); }
}
