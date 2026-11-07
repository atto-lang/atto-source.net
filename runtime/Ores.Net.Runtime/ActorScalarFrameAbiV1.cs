using System.Buffers.Binary;

namespace Ores.Net.Runtime;

/// <summary>
/// Versioned native-byte ABI for a bounded actor-owned scalar continuation.
/// All access is through a current ActorContext; no CLR stack capture, JIT,
/// raw pointer, or guest-callable host closure is exposed.
/// </summary>
public static class ActorScalarFrameAbiV1
{
    public const int HeaderBytes = 16;
    public const int SlotBytes = 16;
    public const int MaxValueSlots = 256;
    public const int MaxBlocks = 128;
    private const uint Magic = 0x31465441; // ATF1, little-endian.
    private const byte I64Tag = 1;
    private const byte BoolTag = 2;

    public static int FrameBytes(int slots)
    {
        if (slots <= 0 || slots > MaxValueSlots)
            throw new ArgumentOutOfRangeException(nameof(slots));
        return checked(HeaderBytes + slots * SlotBytes);
    }

    public static ActorStackFrameHandle Push(ActorContext context,
        ReadOnlySpan<byte> declaredTypes, int blockCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        int bytes = FrameBytes(declaredTypes.Length);
        if (blockCount <= 0 || blockCount > MaxBlocks)
            throw new ArgumentOutOfRangeException(nameof(blockCount));

        // The compiler supplies immutable per-SSA-slot kinds. Validate them
        // before native admission: callers cannot retype a slot on a later write.
        foreach (byte kind in declaredTypes)
            if (kind is not (I64Tag or BoolTag))
                throw new FormatException("Unknown actor scalar ABI declared slot type");
        int valueSlots = declaredTypes.Length;
        ActorStackFrameHandle frame = context.PushStackFrame(bytes, alignment: 8);
        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            header.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], valueSlots);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], blockCount);
            // Entry PC is initially 0; the compiler layout selects the entry.
            context.WriteStackFrame(frame, 0, header);
            Span<byte> slot = stackalloc byte[SlotBytes];
            for (int i = 0; i < valueSlots; i++)
            {
                slot.Clear();
                slot[1] = declaredTypes[i];
                context.WriteStackFrame(frame,
                    checked(HeaderBytes + i * SlotBytes), slot);
            }
            return frame;
        }
        catch
        {
            context.PopStackFrame(frame);
            throw;
        }
    }

    public static int ReadProgramCounter(ActorContext context,
        ActorStackFrameHandle frame)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        ReadHeader(context, frame, header);
        int pc = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int blocks = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (pc < 0 || pc >= blocks)
            throw new InvalidOperationException("Invalid actor scalar frame PC");
        return pc;
    }

    public static void SetProgramCounter(ActorContext context,
        ActorStackFrameHandle frame, int pc)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        ReadHeader(context, frame, header);
        int blocks = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (pc < 0 || pc >= blocks)
            throw new ArgumentOutOfRangeException(nameof(pc));
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(value, pc);
        context.WriteStackFrame(frame, 12, value);
    }

    public static void WriteI64(ActorContext context,
        ActorStackFrameHandle frame, int slot, long value)
    {
        int offset = WritableSlotOffset(context, frame, slot, I64Tag);
        Span<byte> data = stackalloc byte[SlotBytes];
        data.Clear();
        data[0] = I64Tag;
        data[1] = I64Tag;
        BinaryPrimitives.WriteInt64LittleEndian(data[8..], value);
        context.WriteStackFrame(frame, offset, data);
    }

    public static long ReadI64(ActorContext context,
        ActorStackFrameHandle frame, int slot)
    {
        Span<byte> data = stackalloc byte[SlotBytes];
        context.ReadStackFrame(frame, SlotOffset(context, frame, slot), data);
        ValidateSlot(data, I64Tag);
        return BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
    }

    public static void WriteBool(ActorContext context,
        ActorStackFrameHandle frame, int slot, bool value)
    {
        int offset = WritableSlotOffset(context, frame, slot, BoolTag);
        Span<byte> data = stackalloc byte[SlotBytes];
        data.Clear();
        data[0] = BoolTag;
        data[1] = BoolTag;
        data[8] = value ? (byte)1 : (byte)0;
        context.WriteStackFrame(frame, offset, data);
    }

    public static bool ReadBool(ActorContext context,
        ActorStackFrameHandle frame, int slot)
    {
        Span<byte> data = stackalloc byte[SlotBytes];
        context.ReadStackFrame(frame, SlotOffset(context, frame, slot), data);
        ValidateSlot(data, BoolTag);
        if (data[8] > 1)
            throw new InvalidOperationException("Noncanonical actor scalar bool");
        for (int i = 9; i < data.Length; i++)
            if (data[i] != 0)
                throw new InvalidOperationException("Noncanonical actor scalar bool padding");
        return data[8] == 1;
    }

    public static void Pop(ActorContext context, ActorStackFrameHandle frame)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        ReadHeader(context, frame, header);
        context.PopStackFrame(frame);
    }

    private static int WritableSlotOffset(ActorContext context,
        ActorStackFrameHandle frame, int slot, byte expected)
    {
        int offset = SlotOffset(context, frame, slot);
        Span<byte> current = stackalloc byte[SlotBytes];
        context.ReadStackFrame(frame, offset, current);
        if (current[1] != expected ||
            (current[0] != 0 && current[0] != expected))
            throw new InvalidOperationException(
                "Actor scalar slot write violates its declared CFG type");
        for (int i = 2; i < 8; i++)
            if (current[i] != 0)
                throw new InvalidOperationException(
                    "Noncanonical actor scalar slot padding");
        return offset;
    }

    private static int SlotOffset(ActorContext context,
        ActorStackFrameHandle frame, int slot)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        ReadHeader(context, frame, header);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (slot < 0 || slot >= count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return checked(HeaderBytes + slot * SlotBytes);
    }

    private static void ReadHeader(ActorContext context,
        ActorStackFrameHandle frame, Span<byte> header)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frame);
        context.ReadStackFrame(frame, 0, header);
        int slots = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int blocks = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            slots <= 0 || slots > MaxValueSlots ||
            blocks <= 0 || blocks > MaxBlocks ||
            frame.Length != HeaderBytes + slots * SlotBytes)
            throw new InvalidOperationException("Invalid actor scalar frame ABI header");
    }

    private static void ValidateSlot(ReadOnlySpan<byte> data, byte expected)
    {
        if (data[0] != expected || data[1] != expected)
            throw new InvalidOperationException("Uninitialized or incorrectly typed actor scalar slot");
        for (int i = 2; i < 8; i++)
            if (data[i] != 0)
                throw new InvalidOperationException("Noncanonical actor scalar slot padding");
    }
}
