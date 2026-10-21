using Ores.Net.Runtime;

internal static class ActorCompressed32ReferenceSelfTest
{
    public static void Run()
    {
        Require(ActorCompressed32Reference.MaximumEncodedStartingOffsetBytes ==
            34_359_738_352L, "compressed32 maximum encoded start");
        Require(ActorCompressed32Reference.MaximumLogicalSpanBytes ==
            34_359_738_360L, "compressed32 maximum logical span");

        Require(ActorCompressed32Reference.Encode(0) == 1u,
            "offset zero is the first non-null word");
        Require(ActorCompressed32Reference.Decode(0u) is null,
            "word zero is null");
        Require(ActorCompressed32Reference.Decode(1u) == 0,
            "word one decodes to actor offset zero");
        Require(ActorCompressed32Reference.Encode(
            ActorCompressed32Reference.MaximumEncodedStartingOffsetBytes) ==
            uint.MaxValue, "maximum aligned actor offset encodes to UINT32_MAX");
        Require(ActorCompressed32Reference.Decode(uint.MaxValue) ==
            ActorCompressed32Reference.MaximumEncodedStartingOffsetBytes,
            "UINT32_MAX decodes unsigned");
        Require(ActorCompressed32Reference.Decode(0x8000_0000u) ==
            0x7fff_ffffL * 8L, "high bit is unsigned, never a negative offset");

        Reject(() => ActorCompressed32Reference.Encode(-8),
            "negative actor offset");
        Reject(() => ActorCompressed32Reference.Encode(3),
            "misaligned actor offset");
        Reject(() => ActorCompressed32Reference.Encode(
            ActorCompressed32Reference.MaximumEncodedStartingOffsetBytes + 8),
            "unrepresentable actor offset");
        Reject(() => ActorCompressed32Reference.DecodeStored(0x1_0000_0000UL),
            "nonzero stored high bits");
        Reject(() => ActorCompressed32Reference.DereferenceChecked(0,
            ActorCompressed32Reference.MaximumLogicalSpanBytes, 8),
            "null cannot be dereferenced");
        Reject(() => ActorCompressed32Reference.DereferenceChecked(uint.MaxValue,
            ActorCompressed32Reference.MaximumEncodedStartingOffsetBytes, 8),
            "access cannot extend beyond logical span");
        Require(ActorCompressed32Reference.DereferenceChecked(1, 8, 8) == 0,
            "first object can occupy offset zero");

        Console.WriteLine("PASS .NET compressed32 actor-relative representation boundaries");
    }

    private static void Reject(Action action, string scenario)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (OverflowException)
        {
            return;
        }

        throw new Exception("compressed32 unexpectedly admitted: " + scenario);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("compressed32 regression: " + scenario);
    }
}
