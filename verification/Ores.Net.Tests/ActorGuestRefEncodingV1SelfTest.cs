using Ores.Net.Runtime;

/// <summary>
/// HOST-only actor guest handle ABI test, not compiler-emitted .atto guest code.
/// </summary>
internal static class ActorGuestRefEncodingV1SelfTest
{
    internal static void Run()
    {
        if (ActorGuestRefEncodingV1.Abi != "generation16_slot16")
            throw new Exception("wrong .NET actor guest ref ABI");
        ActorGuestRefEncodingV1.RequireAbi("generation16_slot16");
        Reject(() => ActorGuestRefEncodingV1.RequireAbi("offset8_plus_one"));
        Reject(() => ActorGuestRefEncodingV1.RequireAbi(""));
        foreach (int slot in new[] {0, 1, 32767, 65534})
        {
            foreach (ushort generation in new ushort[] {1, 0x7fff, 0x8000, 0xffff})
            {
                uint word = ActorGuestRefEncodingV1.Encode(slot, generation);
                if (!ActorGuestRefEncodingV1.TryDecode(word, out int gotSlot, out ushort gotGen) ||
                    gotSlot != slot || gotGen != generation)
                    throw new Exception("unsigned generation/slot round-trip failed");
            }
        }
        if (ActorGuestRefEncodingV1.TryDecode(0, out _, out _))
            throw new Exception("zero must remain null");
        if (ActorGuestRefEncodingV1.TryDecode(0x00010000u, out _, out _))
            throw new Exception("nonzero generation with zero slot is malformed");
        if (ActorGuestRefEncodingV1.TryDecode(0x00000001u, out _, out _))
            throw new Exception("zero generation is malformed");
        Reject(() => ActorGuestRefEncodingV1.Encode(-1, 1));
        Reject(() => ActorGuestRefEncodingV1.Encode(65535, 1));
        Reject(() => ActorGuestRefEncodingV1.Encode(0, 0));
        Console.WriteLine("PASS actor-ref32-net-generation16-slot16-host-codec");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("expected incompatible .NET guest reference format rejection");
    }
}
