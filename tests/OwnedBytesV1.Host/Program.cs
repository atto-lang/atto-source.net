// Host-only CIL/NativeAOT primitive test, not compiled .atto guest execution.
using Atto.Cil.Compiler;

internal static class Program
{
    private static readonly ByteOwnerV1 Owner = new(1, 2, 3);
    private static readonly ByteOwnerV1 Other = new(9, 2, 3);
    private static int checks;

    private static void Check(bool ok)
    {
        checks++;
        if (!ok) throw new InvalidOperationException("byte-buffer test failed");
    }

    private static void Deny(ByteFaultV1 expected, Action action)
    {
        checks++;
        try { action(); }
        catch (ByteFailureV1 err) when (err.Kind == expected) { return; }
        throw new InvalidOperationException("missing expected byte-buffer fault " + expected);
    }

    private static int Main()
    {
        foreach (var order in new[] { ByteOrderV1.Little, ByteOrderV1.Big })
        {
            using var b = new OwnedBytesV1(Owner, 8, 8);
            b.Write(Owner, 1, 4, 0x12345678, order);
            Check(b.Read(Owner, 1, 4, order) == 0x12345678);
            Check(b.Read(Owner, 5, 1, order) == 0);
            foreach (long off in new[] { -1L, 5L, long.MaxValue, long.MinValue })
            {
                Deny(ByteFaultV1.Bounds, () => b.Write(Owner, off, 4, 42, order));
                Check(b.Read(Owner, 1, 4, order) == 0x12345678);
            }
            foreach (long value in new[] { -1L, 4294967296L, long.MaxValue })
            {
                Deny(ByteFaultV1.Value, () => b.Write(Owner, 1, 4, value, order));
                Check(b.Read(Owner, 1, 4, order) == 0x12345678);
            }
            Deny(ByteFaultV1.Owner, () => b.Read(Other, 0, 4, order));
            Deny(ByteFaultV1.Owner, () => b.Write(Other, 0, 4, 1, order));
            b.Retire(Owner);
            Deny(ByteFaultV1.Retired, () => b.Read(Owner, 1, 4, order));
        }
        Deny(ByteFaultV1.Quota, () => _ = new OwnedBytesV1(Owner, -1, 8));
        Deny(ByteFaultV1.Quota, () => _ = new OwnedBytesV1(Owner, 9, 8));
        using (var empty = new OwnedBytesV1(Owner, 0, 0))
        {
            Check(empty.Length(Owner) == 0);
            Deny(ByteFaultV1.Bounds, () => empty.Read(Owner, 0, 1, ByteOrderV1.Little));
        }
        using (var b = new OwnedBytesV1(Owner, 8, 8))
        {
            ulong seed = 0x9e3779b97f4a7c15UL;
            for (int i = 0; i < 100_000; i++)
            {
                seed ^= seed << 7; seed ^= seed >> 9; seed ^= seed << 8;
                long value = (long)(seed & 0xffffffffUL);
                var order = (seed & 1) == 0 ? ByteOrderV1.Little : ByteOrderV1.Big;
                b.Write(Owner, 3, 4, value, order);
                Check(b.Read(Owner, 3, 4, order) == value);
            }
        }
        Console.WriteLine($"ATTO_NET_BYTES_HOST_PASS checks={checks} guest=false");
        return 0;
    }
}
