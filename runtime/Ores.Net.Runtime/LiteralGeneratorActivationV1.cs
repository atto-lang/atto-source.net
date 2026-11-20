namespace Ores.Net.Runtime;

/// <summary>
/// Native-AOT-safe activation for the first literal synchronous generator
/// source slice. The activation owns one authoritative frame plus a compiler-
/// produced immutable yield table. It does not retain a delegate, reflection
/// metadata, generated method, receiver, or native handle.
/// </summary>
public sealed class LiteralGeneratorActivationV1 : IDisposable
{
    private readonly long[] _yields;
    private GeneratorFrameV1 _frame;
    private int _operation;

    public LiteralGeneratorActivationV1(ReadOnlySpan<long> yields)
    {
        if (yields.Length > GeneratorFrameV1.MaxYields)
            throw new ArgumentOutOfRangeException(
                nameof(yields), "generator yield count exceeds the v1 frame budget");
        _yields = yields.ToArray();
        if (_frame.Initialize() != 1)
            throw new InvalidOperationException("generator frame initialization failed");
    }

    public bool IsTerminal => _frame.IsTerminal;

    /// <summary>
    /// Returns 1 and one yielded i64, 0 for sticky DONE, or -1 when an
    /// overlapping operation or invalid frame state is detected.
    /// </summary>
    public int Next(out long value)
    {
        value = 0;
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return -1;
        try
        {
            int status = _frame.Pull(_yields.Length, out int slot);
            if (status == 1)
            {
                if ((uint)slot >= (uint)_yields.Length) return -1;
                value = _yields[slot];
            }
            return status;
        }
        finally
        {
            Volatile.Write(ref _operation, 0);
        }
    }

    /// <summary>
    /// Close is idempotent after terminal completion. A concurrent pull/close
    /// is rejected instead of racing the mutable frame.
    /// </summary>
    public int Close()
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return -1;
        try
        {
            return _frame.Close();
        }
        finally
        {
            Volatile.Write(ref _operation, 0);
        }
    }

    public void Dispose() => _ = Close();
}
