using Ores.Net.Compiler;
using System.Security.Cryptography;
using System.Text;

// Target-only actor/ML forms must fail closed on both scalar and structured
// source paths. These tests do NOT assert a .NET actor scheduler or autograd.
internal static class ActorHierarchyMlAdmissionTests
{
    private static void RejectBoth(string source, string id)
    {
        foreach (var parse in new Action<string>[]
        {
            s => { _ = OresParser.Parse(s); },
            s => { _ = OresStructuredParser.Parse(s); },
        })
        {
            try { parse(source); }
            catch (FormatException ex)
            {
                if (string.IsNullOrWhiteSpace(ex.Message))
                    throw new Exception("Missing actor denial diagnostic: " + id);
                continue;
            }
            throw new Exception("Unsupported source actor/ML form admitted: " + id);
        }
    }

    public static void Run()
    {
        foreach (var (id, source) in new (string, string)[]
        {
            ("actor", "define actor A1 as end"),
            ("shared", "define shared actor A1 as end"),
            ("untrusted", "define untrusted actor A1 as end"),
            ("private", "define isoactor A1 as end"),
            ("inherits", "define actor Parent as end define actor A1 extends Parent as end"),
            ("ordinary-class-parent", "define class Plain as end define actor A1 extends Plain as end"),
            ("interface", "define interface Stage as handle(int x):void; end define actor A1 implements Stage as end"),
            ("trait", "define trait Stage as step():void; end define actor A1 implements Stage as end"),
            ("class-intrinsic", "define class A1 extends Actor as end"),
            ("supervisor", "define supervisor S1 as end"),
            ("sealed-state", "define actor A1 as state { int count = 1; } end"),
            ("on-handler", "define actor A1 as on #backward :: return; end end"),
            ("model", "define model Network as Dense(2, 1) end"),
            ("actor-pipe", "fnc main():int{spawn A() pipe spawn B();return 1;}"),
            ("spawn", "fnc main():int{spawn Worker();return 1;}"),
            ("tensor", "fnc main():int{let x = Tensor.zeros(2,2);return 1;}"),
        })
            RejectBoth(source, id);

        foreach (var text in new[]
        {
            "fnc main():int{return 42;} // define actor A1 as end",
            "fnc main():int{return 42;} /* define model X as Dense(2,1) end */"
        })
        {
            if (OresIrReferenceEvaluator.Evaluate(
                    OresIrLowering.Lower(OresParser.Parse(text))) != 42)
                throw new Exception("Comment-only actor/ML words changed scalar execution");
            _ = OresStructuredParser.Parse(text);
        }

        // Until the actor scheduler and state-loan lowering exist, the
        // byte-identical shared Counter guest is a source admission negative,
        // not a success signal for .NET or Native AOT actor execution.
        var counterPath = Path.Combine(AppContext.BaseDirectory, "actor-counter-target.ores");
        var counterBytes = File.ReadAllBytes(counterPath);
        var gitPrefix = Encoding.ASCII.GetBytes("blob " + counterBytes.Length + "\0");
        var counterBlob = SHA1.HashData(gitPrefix.Concat(counterBytes).ToArray());
        if (!string.Equals(Convert.ToHexString(counterBlob),
                "C49780B4B42251DB20AE9AC864EB8606BF7DA922", StringComparison.Ordinal))
            throw new Exception("Canonical cross-toolchain Counter actor fixture bytes drifted");
        RejectBoth(Encoding.UTF8.GetString(counterBytes), "canonical-counter-guest");

        // This is only a managed reference-evaluator preflight. The separate
        // Linux Native AOT script must publish the real compiler and guest
        // from the exact SHA-pinned .ores file before claiming execution.
        const string portableSource =
            "pub fnc main(): int { return 2 - 1 * (2 * 1 - 42) * 1; }\n";
        var portableIr = OresIrLowering.Lower(OresParser.Parse(portableSource));
        if (OresIrReferenceEvaluator.Evaluate(portableIr) != 42)
            throw new Exception("Canonical scalar guest syntax/result diverged from reference evaluator");

        // This is a hand-derived signed-i64 numeric intersection, not an
        // implementation of Tensor, backprop, actor communication or ML training.
        foreach (var (expression, expected) in new (string, long)[]
        {
            ("5-1*(5*2-6)*2", -3),
            ("3-1*(3*1-5)*1", 5),
            ("0-1*(0*3-6)*3", 18),
            ("7-1*(7*0-6)*0", 7),
        })
        {
            var source = "fnc main():int{return " + expression + ";}";
            var ir = OresIrLowering.Lower(OresParser.Parse(source));
            if (OresIrReferenceEvaluator.Evaluate(ir) != expected)
                throw new Exception("Scalar analytical gradient step mismatch: " + expression);
        }
        try
        {
            var source = "fnc main():int{return 9223372036854775807-1*(9223372036854775807*2-0)*2;}";
            _ = OresIrReferenceEvaluator.Evaluate(OresIrLowering.Lower(OresParser.Parse(source)));
        }
        catch (OverflowException)
        {
            return;
        }
        throw new Exception("Scalar numerical overflow escaped checked OresIR");
    }
}
