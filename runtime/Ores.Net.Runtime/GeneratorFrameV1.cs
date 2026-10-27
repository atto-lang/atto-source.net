namespace Ores.Net.Runtime;

/// <summary>
/// Native-AOT-safe state substrate for compiler-generated synchronous generators.
/// The compiler owns the yielded values and switches on the returned slot; this
/// frame owns only activation state, so it retains no managed object graph,
/// reflection metadata, delegate, native handle, or borrowed receiver.
/// </summary>
public struct GeneratorFrameV1
{
    public const int MaxYields = 4096;
    private const int Fresh = 0;
    private const int Active = 1;
    private const int Terminal = 2;

    private int _state;
    private int _nextSlot;

    public readonly bool IsTerminal => _state == Terminal;

    /// <summary>Returns 1 for the first initialization, 0 after init/terminal.</summary>
    public int Initialize()
    {
        if (_state != Fresh) return 0;
        _state = Active;
        _nextSlot = 0;
        return 1;
    }

    /// <summary>
    /// Returns 1 and a zero-based compiler-owned yield slot, 0 for sticky DONE,
    /// or -1 for an invalid/uninitialized activation or invalid yield budget.
    /// </summary>
    public int Pull(int yieldCount, out int slot)
    {
        slot = -1;
        if (yieldCount < 0 || yieldCount > MaxYields) return -1;
        if (_state == Terminal) return 0;
        if (_state != Active || _nextSlot < 0 || _nextSlot > yieldCount) return -1;
        if (_nextSlot == yieldCount)
        {
            _state = Terminal;
            _nextSlot = 0;
            return 0;
        }
        slot = _nextSlot++;
        return 1;
    }

    /// <summary>
    /// Returns 1 when an active activation is closed, 0 when already terminal,
    /// and -1 when close is attempted before initialization.
    /// </summary>
    public int Close()
    {
        if (_state == Terminal) return 0;
        if (_state != Active) return -1;
        _state = Terminal;
        _nextSlot = 0;
        return 1;
    }
}
