using System.Runtime.InteropServices;

namespace Ores.Net.Runtime;

/// <summary>
/// Native-AOT-safe state substrate for compiler-generated synchronous generators.
/// The compiler owns yielded values and switches on the returned slot. This frame
/// owns only activation state and retains no delegate, native handle, receiver,
/// reflection metadata, or guest object graph.
///
/// This is trusted compiler/runtime state, not a public capability boundary.
/// Compiler lowering must keep one authoritative storage location and pass it by
/// reference; copying the struct would create an independent snapshot and is not
/// a valid generator activation alias.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GeneratorFrameV1
{
    public const int MaxYields = 4096;
    private const int Fresh = 0;
    private const int Active = 1;
    private const int Terminal = 2;

    private int _state;
    private int _nextSlot;

    public readonly bool IsTerminal => _state == Terminal && _nextSlot == 0;

    public int Initialize()
    {
        if (_state == Fresh)
        {
            _state = Active;
            _nextSlot = 0;
            return 1;
        }
        if (_state == Active)
            return _nextSlot >= 0 && _nextSlot <= MaxYields ? 0 : -1;
        if (_state == Terminal)
            return _nextSlot == 0 ? 0 : -1;
        return -1;
    }

    public int Pull(int yieldCount, out int slot)
    {
        slot = -1;
        if (yieldCount < 0 || yieldCount > MaxYields) return -1;
        if (_state == Terminal) return _nextSlot == 0 ? 0 : -1;
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

    public int Close()
    {
        if (_state == Terminal) return _nextSlot == 0 ? 0 : -1;
        if (_state != Active) return -1;
        _state = Terminal;
        _nextSlot = 0;
        return 1;
    }
}
