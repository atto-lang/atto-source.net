namespace Ores.Net.Runtime.Net;

/// <summary>
/// Host-only immutable TCP slab with checked one-use pin receipts. The bytes
/// are copied at publication; no pointer, mutable memory or socket is exposed
/// to guest actors. The gate is only for metadata and bounded byte reads,
/// never a mailbox or physical I/O wait.
/// </summary>
internal sealed class FrozenTcpSlab
{
    internal const int MaxBytes = 1024 * 1024;

    internal sealed class Pin
    {
        internal readonly FrozenTcpSlab Owner;
        internal bool Active = true;
        internal Pin(FrozenTcpSlab owner) => Owner = owner;
    }

    private readonly object gate = new();
    private byte[]? bytes;
    private readonly int size;
    private int pins;
    private bool retiring;

    internal FrozenTcpSlab(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(input), "TCP slab exceeds one MiB");
        bytes = (byte[])input.Clone();
        size = input.Length;
    }

    internal int Size => size;

    internal Pin AcquirePin()
    {
        lock (gate)
        {
            if (retiring) throw new InvalidOperationException("slab retired");
            if (pins == int.MaxValue) throw new InvalidOperationException("pin count exhausted");
            pins++;
            return new Pin(this);
        }
    }

    internal int ReadByte(Pin? pin, int index)
    {
        lock (gate)
        {
            if (pin is null || !ReferenceEquals(pin.Owner, this) || !pin.Active)
                throw new InvalidOperationException("invalid TCP slab pin");
            var snapshot = bytes ?? throw new InvalidOperationException("TCP slab storage retired");
            if ((uint)index >= (uint)snapshot.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return snapshot[index];
        }
    }

    internal void ReleasePin(Pin? pin)
    {
        lock (gate)
        {
            if (pin is null || !ReferenceEquals(pin.Owner, this) || !pin.Active)
                throw new InvalidOperationException("invalid TCP slab pin release");
            pin.Active = false;
            pins--;
            FinalizeRetirement();
        }
    }

    internal void Retire()
    {
        lock (gate)
        {
            retiring = true;
            FinalizeRetirement();
        }
    }

    internal bool Freed
    {
        get { lock (gate) return bytes is null; }
    }

    internal int PinCount
    {
        get { lock (gate) return pins; }
    }

    private void FinalizeRetirement()
    {
        // Reclamation eligibility, NOT guaranteed immediate zeroization.
        // Only a trusted host with physically quiesced IO may call Retire.
        if (retiring && pins == 0) bytes = null;
    }
}
