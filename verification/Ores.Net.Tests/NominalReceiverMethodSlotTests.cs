using Ores.Net.Compiler;

internal static class NominalReceiverMethodSlotTests
{
    private static void Require(bool truth, string why)
    {
        if (!truth) throw new Exception("Nominal receiver slot contract: " + why);
    }

    private static void Reject(Action action, string diagnostic)
    {
        try { action(); }
        catch (FormatException error) when (
            error.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected receiver slot rejection containing: " + diagnostic);
    }

    public static void Run()
    {
        foreach (var owner in new[] { "mod.Foo", "mod.WorkerActor" })
        {
            var slots = new NominalReceiverMethodSlots();
            var first = slots.Declare(owner, "x", 0);
            Require(first.ValueArity == 0 && slots.Contains(first), "implicit x/0");
            Reject(() => slots.Declare(owner, "x", 0, explicitSelf: true, mutableSelf: true),
                "x/0");
            Reject(() => slots.Declare(owner, "x", 0, explicitSelf: true), "x/0");

            var value = slots.Declare(owner, "x", 1);
            Require(value.ValueArity == 1, "hidden receiver excluded from x/1");
            Reject(() => slots.Declare(owner, "x", 1, explicitSelf: true, mutableSelf: true),
                "x/1");
            Reject(() => slots.Declare(owner, "x", 1, explicitSelf: true), "x/1");

            // Different names are not overload conflicts, even with same arity.
            var y = slots.Declare(owner, "y", 1, explicitSelf: true, mutableSelf: true);
            Require(slots.Contains(y), "distinct method names remain distinct");
            var stat = slots.Declare(owner, "x", 1, ReceiverMethodNamespace.Static);
            Require(stat.Namespace == ReceiverMethodNamespace.Static, "static and instance keys differ");
            var otherOwner = slots.Declare("other.Foo", "x", 1);
            Require(otherOwner.Owner == "other.Foo", "owner is part of method selector");

            var reversed = new NominalReceiverMethodSlots();
            reversed.Declare(owner, "x", 0, explicitSelf: true, mutableSelf: true);
            Reject(() => reversed.Declare(owner, "x", 0), "x/0");
            reversed.Declare(owner, "x", 1, explicitSelf: true, mutableSelf: true);
            Reject(() => reversed.Declare(owner, "x", 1), "x/1");
        }

        // The parser/lowerer supplies physical parameter count, including
        // the generated self loan for instance methods. The adapter must
        // erase exactly one receiver and preserve all explicit values.
        var lowered = new NominalReceiverMethodSlots();
        var instance = lowered.DeclareLowered("Foo", "x", 2,
            explicitSelf: true, mutableSelf: true);
        Require(instance.ValueArity == 1, "injected receiver counted as caller arity");
        Reject(() => lowered.DeclareLowered("Foo", "x", 2), "x/1");
        var noValues = lowered.DeclareLowered("Foo", "x", 1);
        Require(noValues.ValueArity == 0, "injected-only receiver must be x/0");
        Reject(() => lowered.DeclareLowered("Foo", "x", 1,
            explicitSelf: true, mutableSelf: true), "x/0");

        // Inline and split mutable-self syntax must lower to the exact same
        // method selector: source spelling is not an overload dimension.
        var splitOrInline = new NominalReceiverMethodSlots();
        splitOrInline.DeclareLowered("Foo", "y", 2,
            explicitSelf: true, mutableSelf: true);
        Reject(() => splitOrInline.DeclareLowered("Foo", "y", 2,
            explicitSelf: true, mutableSelf: true), "y/1");

        // The registry is a host-only selector contract, not a claim that
        // the structured parser accepts methods with 1,024 parameters.
        // Sweep key uniqueness and exactly-one hidden receiver erasure.
        var swept = new NominalReceiverMethodSlots();
        for (int arity = 0; arity <= 1024; arity++)
        {
            var key = swept.DeclareLowered("Foo", "measure", arity + 1);
            Require(key.ValueArity == arity && swept.Contains(key),
                "receiver erasure or selector uniqueness drifted at " + arity);
            Reject(() => swept.Declare("Foo", "measure", arity,
                explicitSelf: true, mutableSelf: true), "measure/" + arity);
        }
        var maximum = new NominalReceiverMethodSlots().DeclareLowered(
            "Foo", "edge", int.MaxValue);
        Require(maximum.ValueArity == int.MaxValue - 1,
            "maximum lowered arity must erase exactly one receiver");

        var staticMethod = lowered.DeclareLowered("Foo", "x", 2,
            ReceiverMethodNamespace.Static);
        Require(staticMethod.ValueArity == 2, "static method count was incorrectly erased");
        Reject(() => new NominalReceiverMethodSlots().DeclareLowered("Foo", "x", 0),
            "missing compiler-injected self");
        Reject(() => new NominalReceiverMethodSlots().DeclareLowered("Foo", "x", -1),
            "nonnegative");
        Reject(() => new NominalReceiverMethodSlots().DeclareLowered("Foo", "x", 1,
            ReceiverMethodNamespace.Static, explicitSelf: true), "static method");

        Reject(() => new NominalReceiverMethodSlots().Declare("", "x", 0), "nonempty");
        Reject(() => new NominalReceiverMethodSlots().Declare("Foo", "", 0), "nonempty");
        Reject(() => new NominalReceiverMethodSlots().Declare("Foo", "x", -1), "nonnegative");
        Reject(() => new NominalReceiverMethodSlots().Declare("Foo", "x", 0,
            ReceiverMethodNamespace.Static, explicitSelf: true), "static method");
        Reject(() => new NominalReceiverMethodSlots().Declare("Foo", "x", 0,
            mutableSelf: true), "explicit receiver");
        Reject(() => new NominalReceiverMethodSlots().Declare("Foo", "x", 0,
            (ReceiverMethodNamespace)99), "namespace");
    }
}
