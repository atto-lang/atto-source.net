using Ores.Net.Runtime.Net;

internal static class FrozenTcpSlabSelfTest
{
    private static void Check(bool value, string why)
    {
        if (!value) throw new Exception("Portable TCP slab: " + why);
    }
    private static void Reject<T>(Action action, string why) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected rejection: " + why);
    }

    internal static void Run()
    {
        var source = new byte[] { 0, 1, 127, 128, 255 };
        var slab = new FrozenTcpSlab(source);
        Array.Fill(source, (byte)0);
        var first = slab.AcquirePin();
        var second = slab.AcquirePin();
        Check(slab.Size == 5 && slab.ReadByte(first, 3) == 128 &&
              slab.ReadByte(first, 4) == 255, "canonical bytes remain unchanged");
        Reject<ArgumentOutOfRangeException>(() => slab.ReadByte(first, -1),
            "negative index");
        Reject<ArgumentOutOfRangeException>(() => slab.ReadByte(first, 5),
            "out of bounds");
        Reject<InvalidOperationException>(() => slab.ReadByte(null, 0),
            "missing pin");
        var foreign = new FrozenTcpSlab(new byte[] { 42 });
        Reject<InvalidOperationException>(() => foreign.ReadByte(first, 0),
            "foreign slab pin");
        Reject<InvalidOperationException>(() => foreign.ReleasePin(first),
            "foreign slab release");
        slab.Retire();
        Reject<InvalidOperationException>(() => slab.AcquirePin(),
            "no new pins after retirement");
        slab.ReleasePin(first);
        Check(!slab.Freed && slab.ReadByte(second, 3) == 128,
            "remaining pin survives retirement");
        Reject<InvalidOperationException>(() => slab.ReleasePin(first),
            "double pin release");
        Reject<InvalidOperationException>(() => slab.ReadByte(first, 3),
            "released pin cannot read");
        slab.ReleasePin(second);
        Check(slab.Freed && slab.PinCount == 0, "quiescence");
        Reject<ArgumentOutOfRangeException>(() =>
            new FrozenTcpSlab(new byte[FrozenTcpSlab.MaxBytes + 1]),
            "one MiB maximum");
        var empty = new FrozenTcpSlab(Array.Empty<byte>());
        var emptyPin = empty.AcquirePin();
        Reject<ArgumentOutOfRangeException>(() => empty.ReadByte(emptyPin, 0),
            "empty slab is not readable");
        empty.ReleasePin(emptyPin);
        empty.Retire();
        Check(empty.Freed, "empty slab retired");
    }
}
