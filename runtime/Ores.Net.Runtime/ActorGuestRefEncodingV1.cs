namespace Ores.Net.Runtime;

/// <summary>
/// Explicit generation16_slot16 actor-local 32-bit guest handle format.
/// This is *not* an offset8_plus_one Ref32 or globally authenticated capability.
/// The owning ActorArena must separately validate incarnation, turn, allocation,
/// type, bounds, borrow state and all cross-actor transfer boundaries.
/// </summary>
internal static class ActorGuestRefEncodingV1
{
    internal const string Abi = "generation16_slot16";
    internal const int SlotBits = 16;
    internal const uint SlotMask = ushort.MaxValue;
    internal const int MaxSlots = ushort.MaxValue;

    internal static void RequireAbi(string requested)
    {
        if (!string.Equals(requested, Abi, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "E_ACTOR_MEMORY_ABI: requested representation is not generation16_slot16");
    }

    internal static uint Encode(int slot, ushort generation)
    {
        if (slot < 0 || slot >= MaxSlots || generation == 0)
            throw new InvalidOperationException("E_ACTOR_MEMORY_ABI: invalid slot or generation");
        return ((uint)generation << SlotBits) | checked((uint)(slot + 1));
    }

    internal static bool TryDecode(uint word, out int slot, out ushort generation)
    {
        uint encodedSlot = word & SlotMask;
        uint encodedGeneration = word >> SlotBits;
        slot = -1;
        generation = 0;
        if (encodedSlot == 0 || encodedGeneration == 0)
            return false;
        slot = checked((int)encodedSlot - 1);
        generation = checked((ushort)encodedGeneration);
        return true;
    }
}
