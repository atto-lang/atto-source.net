using System.Reflection;
using System.Runtime.Loader;
using Ores.Net.Compiler;

/// <summary>
/// atto-spec bitwise-and-stream-operators-v1 on the .NET structured CIL path:
/// `<<<`, `>>>` and infix `ashr`, executed by the real CLR (not only the test
/// CIL interpreter), with the 0..63 distance guard and `<<`/`>>` rejected.
/// </summary>
internal static class ShiftOperatorTests
{
    private static long Execute(string source, out Exception? failure)
    {
        var graph = OresStructuredCfgLowering.Lower(source);
        string folder = Path.Combine(Path.GetTempPath(), "atto-net-shift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var context = new AssemblyLoadContext("atto-shift", isCollectible: true);
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitLibrary(graph, output);
            var assembly = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(output)));
            var run = assembly.GetType("Oreslang.Generated.OresProgram")!
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
            failure = null;
            try { return (long)run.Invoke(null, null)!; }
            catch (TargetInvocationException thrown) { failure = thrown.InnerException; return 0; }
        }
        finally
        {
            context.Unload();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Expect(string body, long expected, string label)
    {
        string source = "fnc main():int{" + body + "}";
        long reference = CfgReferenceEvaluator.Evaluate(OresStructuredCfgLowering.Lower(source));
        if (reference != expected)
            throw new Exception($"shift reference mismatch {label}: {reference} != {expected}");
        long actual = Execute(source, out var failure);
        if (failure is not null)
            throw new Exception($"shift CLR execution failed {label}: {failure}");
        if (actual != expected)
            throw new Exception($"shift CLR mismatch {label}: {actual} != {expected}");
    }

    private static void ExpectTrap(string body, string label)
    {
        string source = "fnc main():int{" + body + "}";
        _ = Execute(source, out var failure);
        if (failure is not OverflowException)
            throw new Exception($"shift distance must raise OverflowException {label}: {failure}");
        bool referenceTraps = false;
        try { _ = CfgReferenceEvaluator.Evaluate(OresStructuredCfgLowering.Lower(source)); }
        catch (OverflowException) { referenceTraps = true; }
        if (!referenceTraps)
            throw new Exception("reference evaluator must trap too: " + label);
    }

    private static void Reject(string body, string fragment, string label)
    {
        try { _ = OresStructuredCfgLowering.Lower("fnc main():int{" + body + "}"); }
        catch (FormatException rejected)
        {
            if (!rejected.Message.Contains(fragment, StringComparison.Ordinal))
                throw new Exception($"wrong rejection for {label}: {rejected.Message}");
            return;
        }
        throw new Exception("shift source must be rejected: " + label);
    }

    public static void Run()
    {
        // Runtime operands (let bindings), so the CIL guard and shift run.
        Expect("let int a=8;let int b=2;return a <<< b;", 32, "BW-005");
        Expect("let int a=-1;let int b=1;return a >>> b;", long.MaxValue, "BW-006");
        Expect("let int a=-8;let int b=1;return a ashr b;", -4, "BW-007");
        Expect("let int a=1;let int b=2;let int c=3;return a + b <<< c;", 24, "BW-009 precedence");
        Expect("let int one=1;let int s=63;return (one <<< s) >>> s;", 1, "BW-010");
        Expect("let int one=1;let int s=63;return (one <<< s) ashr s;", -1, "BW-011");
        Expect("let int a=65535;let int s=8;return a ashr s;", 255, "ODB1 high byte");
        Expect("let int a=1;let int s=4;if a <<< s > 15 { return 1; } else { return 0; }", 1, "shift binds tighter than comparison");
        Expect("let int a=5;let int s=0;return a <<< s;", 5, "zero distance");
        // Constant operands fold at compile time with the same results.
        Expect("return 8 <<< 2;", 32, "folded left shift");
        Expect("return -1 >>> 63;", 1, "folded logical right shift");
        Expect("return -256 ashr 8;", -1, "folded arithmetic right shift");

        ExpectTrap("let int one=1;let int d=64;return one <<< d;", "distance 64");
        ExpectTrap("let int a=255;let int d=-1;return a ashr d;", "negative distance");
        ExpectTrap("let int a=-1;let int d=9223372036854775807;return a >>> d;", "huge distance");

        Reject("return 1 << 2;", "typed channel/stream I/O", "<< is not a shift");
        Reject("return 8 >> 2;", "typed channel/stream I/O", ">> is not a shift");
        Reject("return true <<< 1;", "Shift requires int operands", "bool operand");
        Reject("return 1 ashr true;", "Shift requires int operands", "bool distance");
        Reject("const int x = 1 <<< 64;return x;", "outside 0..63", "const distance 64");

        // Compile actual structured source and invoke its emitted PE on the
        // CLR. The separate CIL interpreter cannot prove CLR stack validity.
        Expect("let int a=12;let int b=10;return a ^^ b;", 6, "BW-003 runtime XOR");
        Expect("let int a=-9223372036854775808;let int b=-1;a ^^=b;return a;", long.MaxValue, "compound MIN/MAX");
        Expect("let int a=9223372036854775807;a ^^= -1;return a;", long.MinValue, "compound MAX/MIN");
        Expect("let int a=-9223372036854775808;a ^^=a;return a;", 0, "compound self XOR");
        Expect("let int a=9007199254740993;a ^^=9007199254740992;return a;", 1, "compound wide precision");
        Expect("let int a=12;if true {a ^^=10;} return a;", 6, "compound branch SSA join");
        Expect("let int a=12;let int n=0;while n<2 {a ^^=10;n=n+1;}return a;", 12, "compound loop SSA backedge");
        Expect("return Xor(true,false) ? 42 : 0;", 42, "named Boolean XOR");
        ExpectTrap("let int a=9223372036854775807;a ^^=a+1;return a;", "compound checked RHS overflow");
        Reject("return true ^^ false ? 1 : 0;", "Integer XOR requires int operands", "Boolean punctuation XOR");
        Reject("let bool a=true;a ^^=false;return 0;", "Compound XOR requires a mutable int local", "Boolean compound XOR");
        Reject("val int a=12;a ^^=10;return a;", "E_IMMUTABLE_REBIND", "immutable compound XOR");

        // Exercise the actual emitted assembly, not merely an AST or interpreter.
        Expect("let int x=21;set x <<<=1;return x;",42,"explicit left");
        Expect("let int x=84;set x >>>=1;return x;",42,"explicit logical right");
        Expect("let int x=-84;set x ashr=1;return 84+x;",42,"explicit arithmetic right");
        Expect("let int x=40;set x ^^=2;return x;",42,"explicit XOR");
        Expect("let int x=21;for let i=1;i<2;set i <<<=1{set x <<<=1;}return x;",42,"explicit shift loop");
        Expect("let int x=-1;set x >>>=63;return x;",1,"logical sign bit");
        Expect("let int x=1;set x <<<=63;return x;",long.MinValue,"left sign bit");
        ExpectTrap("let int x=1;let int d=64;set x <<<=d;return x;","explicit distance 64");
        ExpectTrap("let int x=1;let int d=-1;set x ashr=d;return x;","explicit negative distance");
        ExpectTrap("let int x=1;let int d=9223372036854775807;set x >>>=d;return x;","explicit huge distance");
        Reject("val int x=21;set x <<<=1;return x;","E_IMMUTABLE_REBIND","fixed shift");
        Reject("let bool x=true;set x <<<=1;return 42;","Compound update requires a mutable int local","bool shift");
        Reject("let int x=21;set x <<< =1;return x;","Expected","spaced equals");
        Reject("let int x=21;set x ashr =1;return x;","Expected","spaced ashr equals");
        Reject("let int x=21;set x << =1;return x;","Expected","stream operator cannot be shift");
        Console.WriteLine("EXPLICIT_SHIFT_COMPOUND_CLR_PASS positives=7 traps=3 rejects=5");
        Console.WriteLine("PASS ShiftOperatorTests: checked shifts and canonical integer/compound XOR on the real CLR");
    }
}
