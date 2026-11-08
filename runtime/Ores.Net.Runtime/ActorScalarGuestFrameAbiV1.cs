using System.Buffers.Binary;

namespace Ores.Net.Runtime;

/// <summary>
/// Compiler-facing typed scalar frame ABI. Unlike the failure-neutral
/// ActorScalarFrameAbiV1 host API, EVERY native access uses the terminal
/// Guest* turn API. Type/PC/header violations latch the actor's guest turn
/// even if a host embedding catches the original exception.
/// This is an ABI seam for verified scalar CFGs, NOT actor source lowering,
/// await/resume, a CLR stack switch, or compiled .atto actor execution.
/// </summary>
public static class ActorScalarGuestFrameAbiV1
{
    public const int HeaderBytes = ActorScalarFrameAbiV1.HeaderBytes;
    public const int SlotBytes = ActorScalarFrameAbiV1.SlotBytes;
    private const uint Magic = 0x31465441; // ATF1, little-endian.
    private const byte I64 = 1;
    private const byte Bool = 2;

    public static ActorStackFrameHandle Push(ActorContext context,
        ReadOnlySpan<byte> declaredTypes, int blockCount, int entryBlock = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        int count = declaredTypes.Length;
        if (count is <= 0 or > ActorScalarFrameAbiV1.MaxValueSlots ||
            blockCount is <= 0 or > ActorScalarFrameAbiV1.MaxBlocks ||
            entryBlock < 0 || entryBlock >= blockCount)
            context.RejectGuestFrame("Invalid verified scalar guest frame dimensions");
        foreach (byte type in declaredTypes)
            if (type is not (I64 or Bool))
                context.RejectGuestFrame("Unsupported verified scalar guest slot type");

        // No new native reservation: the same actor-owned frame slab and
        // quota counter are used by all compiler-owned guest activations.
        var frame = context.GuestPushStackFrame(checked(HeaderBytes + count * SlotBytes));
        Span<byte> bytes = stackalloc byte[HeaderBytes];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], blockCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], entryBlock);
        context.GuestWriteStackFrame(frame, 0, bytes);

        Span<byte> slot = stackalloc byte[SlotBytes];
        for (int i = 0; i < count; i++)
        {
            slot.Clear();
            slot[1] = declaredTypes[i]; // immutable verified SSA slot kind
            context.GuestWriteStackFrame(frame,
                checked(HeaderBytes + i * SlotBytes), slot);
        }
        return frame;
    }

    public static int ReadProgramCounter(ActorContext context,
        ActorStackFrameHandle frame) => ReadHeader(context, frame).Pc;

    public static void SetProgramCounter(ActorContext context,
        ActorStackFrameHandle frame, int pc)
    {
        var header = ReadHeader(context, frame);
        if (pc < 0 || pc >= header.Blocks)
            context.RejectGuestFrame("Verified scalar guest PC outside CFG bounds");
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, pc);
        context.GuestWriteStackFrame(frame, 12, bytes);
    }

    public static void WriteI64(ActorContext context,
        ActorStackFrameHandle frame, int slot, long value)
    {
        int offset = WritableSlot(context, frame, slot, I64);
        Span<byte> encoded = stackalloc byte[SlotBytes];
        encoded.Clear();
        encoded[0] = I64;
        encoded[1] = I64;
        BinaryPrimitives.WriteInt64LittleEndian(encoded[8..], value);
        context.GuestWriteStackFrame(frame, offset, encoded);
    }

    public static long ReadI64(ActorContext context,
        ActorStackFrameHandle frame, int slot)
    {
        Span<byte> bytes = stackalloc byte[SlotBytes];
        ReadSlot(context, frame, slot, bytes);
        RequireInitialized(context, bytes, I64);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
    }

    public static void WriteBool(ActorContext context,
        ActorStackFrameHandle frame, int slot, bool value)
    {
        int offset = WritableSlot(context, frame, slot, Bool);
        Span<byte> encoded = stackalloc byte[SlotBytes];
        encoded.Clear();
        encoded[0] = Bool;
        encoded[1] = Bool;
        encoded[8] = value ? (byte)1 : (byte)0;
        context.GuestWriteStackFrame(frame, offset, encoded);
    }

    public static bool ReadBool(ActorContext context,
        ActorStackFrameHandle frame, int slot)
    {
        Span<byte> bytes = stackalloc byte[SlotBytes];
        ReadSlot(context, frame, slot, bytes);
        RequireInitialized(context, bytes, Bool);
        if (bytes[8] > 1)
            context.RejectGuestFrame("Noncanonical verified scalar guest bool");
        for (int i = 9; i < SlotBytes; i++)
            if (bytes[i] != 0)
                context.RejectGuestFrame("Nonzero verified scalar guest bool padding");
        return bytes[8] == 1;
    }

    public static void Pop(ActorContext context, ActorStackFrameHandle frame)
    {
        _ = ReadHeader(context, frame);
        context.GuestPopStackFrame(frame);
    }

    private static int WritableSlot(ActorContext context,
        ActorStackFrameHandle frame, int slot, byte declaredType)
    {
        Span<byte> bytes = stackalloc byte[SlotBytes];
        int offset = ReadSlot(context, frame, slot, bytes);
        if (bytes[1] != declaredType ||
            (bytes[0] != 0 && bytes[0] != declaredType))
            context.RejectGuestFrame("Verified scalar guest slot retyping rejected");
        for (int i = 2; i < 8; i++)
            if (bytes[i] != 0)
                context.RejectGuestFrame("Noncanonical verified scalar guest slot padding");
        if (bytes[0] == 0)
            for (int i = 8; i < SlotBytes; i++)
                if (bytes[i] != 0)
                    context.RejectGuestFrame("Uninitialized verified scalar guest slot contains data");
        return offset;
    }

    private static void RequireInitialized(ActorContext context,
        ReadOnlySpan<byte> bytes, byte type)
    {
        if (bytes[0] != type || bytes[1] != type)
            context.RejectGuestFrame("Uninitialized or retyped verified scalar guest read");
        for (int i = 2; i < 8; i++)
            if (bytes[i] != 0)
                context.RejectGuestFrame("Noncanonical verified scalar guest slot padding");
    }

    private static int ReadSlot(ActorContext context, ActorStackFrameHandle frame,
        int slot, Span<byte> destination)
    {
        var header = ReadHeader(context, frame);
        if (slot < 0 || slot >= header.Slots)
            context.RejectGuestFrame("Verified scalar guest SSA ID outside native frame");
        int offset = checked(HeaderBytes + slot * SlotBytes);
        context.GuestReadStackFrame(frame, offset, destination);
        return offset;
    }

    private static (int Slots, int Blocks, int Pc) ReadHeader(
        ActorContext context, ActorStackFrameHandle frame)
    {
        ArgumentNullException.ThrowIfNull(context);
        Span<byte> header = stackalloc byte[HeaderBytes];
        // Stale, off-carrier and foreign handles are authenticated before
        // any untrusted header bytes are consumed or actor state changed.
        context.GuestReadStackFrame(frame, 0, header);
        int slots = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int blocks = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int pc = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            slots is <= 0 or > ActorScalarFrameAbiV1.MaxValueSlots ||
            blocks is <= 0 or > ActorScalarFrameAbiV1.MaxBlocks ||
            pc < 0 || pc >= blocks ||
            frame.Length != HeaderBytes + slots * SlotBytes)
            context.RejectGuestFrame("Corrupt verified scalar guest frame header");
        return (slots, blocks, pc);
    }
}
