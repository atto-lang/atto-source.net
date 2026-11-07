using Ores.Net.Compiler;

internal static class CfgActorFrameLayoutSelfTest
{
    public static void Run()
    {
        // Only verified, frozen SSA may generate a frame layout. These are
        // compiler-owned typed slots, NOT executable .atto actor frames.
        var mutable = new[]
        {
            new CfgInstruction(0, CfgOpcode.ConstantBool, IrValueType.Bool,
                BoolConstant: true),
            new CfgInstruction(1, CfgOpcode.ConstantI64, IrValueType.I64,
                I64Constant: 42)
        };
        var input = new CfgFunction("main", IrValueType.I64, "entry",
            new[] { new CfgBlock("entry", Array.Empty<CfgParameter>(),
                mutable, new CfgReturn(1)) });
        var verified = OresCfgVerifier.Verify(input);
        var layout = CfgActorFrameLayoutPlanner.Plan(verified, maximumFrameBytes: 16);
        Require(layout.ByteLength == 16 && layout.Slots.Count == 2,
            "two typed values occupy two bounded native scalar slots");
        Require(layout.Slots[0] == new CfgActorFrameSlot(
                    0, IrValueType.Bool, 0, 8) &&
                layout.Slots[1] == new CfgActorFrameSlot(
                    1, IrValueType.I64, 8, 8),
            "SSA slot offsets and scalar types remain deterministic");

        // No stale alias to unverified input may mutate the frame descriptor.
        mutable[0] = new CfgInstruction(0, CfgOpcode.ConstantI64,
            IrValueType.I64, I64Constant: 123);
        Require(layout.Slots[0].Type == IrValueType.Bool &&
                CfgActorFrameLayoutPlanner.Plan(verified, 16).Slots[0].Type ==
                    IrValueType.Bool,
            "verified graph snapshot prevents post-verification type drift");

        ExpectFrameError(() => CfgActorFrameLayoutPlanner.Plan(verified, 15),
            "frame byte budget must reject an otherwise valid typed graph");
        ExpectFrameError(() => CfgActorFrameLayoutPlanner.Plan(verified, 8),
            "small per-actor frame budget must fail closed");
        bool rejectedLimit = false;
        try
        {
            _ = CfgActorFrameLayoutPlanner.Plan(verified,
                CfgActorFrameLayoutPlanner.MaximumFrameBytes + 1);
        }
        catch (ArgumentOutOfRangeException) { rejectedLimit = true; }
        Require(rejectedLimit, "invalid maximum frame bound rejected before planning");

        Console.WriteLine(
            "PASS .NET compiler typed CFG actor-frame layout plan (no CIL spill or .atto actor guest executed)");
    }

    private static void ExpectFrameError(Action action, string scenario)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Expected typed frame layout rejection: " + scenario);
    }

    private static void Require(bool value, string scenario)
    {
        if (!value)
            throw new Exception("Compiler actor frame layout regression: " + scenario);
    }
}
