using Ores.Net.Compiler;

public static class ManagedSourceEmitterTests
{
    private static void Require(bool actual, string why)
    {
        if (!actual) throw new Exception("Managed source emitter: " + why);
    }

    private static void Reject(Action action, string why)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Expected managed source rejection: " + why);
    }

    public static void Run()
    {
        const string source = "define pub fnc main() : int { return 2 + 3 * 4; }";
        var ir = OresIrLowering.Lower(OresParser.Parse(source));
        var cs = ManagedSourceEmitter.Emit(ir, ManagedSourceTarget.CSharp);
        var fs = ManagedSourceEmitter.Emit(ir, ManagedSourceTarget.FSharp);

        Require(cs.Contains("namespace Oreslang.Generated;", StringComparison.Ordinal) &&
                cs.Contains("public static long Run()", StringComparison.Ordinal),
            "C# static ABI matches OresProgram.Run");
        Require(cs.Contains("long v3 = checked(v1 * v2);", StringComparison.Ordinal) &&
                cs.Contains("long v4 = checked(v0 + v3);", StringComparison.Ordinal) &&
                cs.Contains("return v4;", StringComparison.Ordinal),
            "C# emits checked ordered SSA operations");

        Require(fs.Contains("namespace Oreslang.Generated", StringComparison.Ordinal) &&
                fs.Contains("open Microsoft.FSharp.Core.Operators.Checked", StringComparison.Ordinal) &&
                fs.Contains("static member Run() : int64 =", StringComparison.Ordinal),
            "F# ABI and checked operator module");
        Require(fs.Contains("let v3 : int64 = v1 * v2", StringComparison.Ordinal) &&
                fs.Contains("let v4 : int64 = v0 + v3", StringComparison.Ordinal) &&
                fs.EndsWith("        v4" + Environment.NewLine, StringComparison.Ordinal),
            "F# emits ordered SSA bindings");

        Require(cs == ManagedSourceEmitter.Emit(ir, ManagedSourceTarget.CSharp) &&
                fs == ManagedSourceEmitter.Emit(ir, ManagedSourceTarget.FSharp),
            "repeated code generation is deterministic");
        Require(OresIrReferenceEvaluator.Evaluate(ir) == 14,
            "source emitters share verified IR with reference evaluator");

        var minimum = OresIrLowering.Lower(OresParser.Parse(
            "fnc main():int{return -9223372036854775808;}"));
        Require(ManagedSourceEmitter.Emit(minimum, ManagedSourceTarget.CSharp)
                .Contains("long.MinValue", StringComparison.Ordinal) &&
                ManagedSourceEmitter.Emit(minimum, ManagedSourceTarget.FSharp)
                .Contains("System.Int64.MinValue", StringComparison.Ordinal),
            "i64 min is generated without overflowing source literal parsing");

        var overflow = OresIrLowering.Lower(OresParser.Parse(
            "fnc main():int{return 9223372036854775807 + 1;}"));
        Require(ManagedSourceEmitter.Emit(overflow, ManagedSourceTarget.CSharp)
                .Contains("checked(v0 + v1)", StringComparison.Ordinal) &&
                ManagedSourceEmitter.Emit(overflow, ManagedSourceTarget.FSharp)
                .Contains("Operators.Checked", StringComparison.Ordinal),
            "overflow checks must never be dropped");

        Reject(() => ManagedSourceEmitter.Emit(ir, (ManagedSourceTarget)9),
            "unknown target rejected");
        Reject(() => OresParser.Parse("fnc main():int{return 3/2;}"),
            "source int/int division still rejected, not silently lowered to i64");
        Reject(() => OresParser.Parse("fnc main():int{return missing(1);}"),
            "unverified function calls still rejected");
        Reject(() => OresParser.Parse("fnc main():int{return nb select first {}; }"),
            "actor/select syntax still rejected");
    }
}
