using Mono.Cecil;
using Ores.Net.Compiler;

// Real checked structured Atto source -> CFG -> emitted CIL test-interpreter
// evidence only. NOT Linux/Windows PublishAot and NOT actor/stream transport.
internal static class DirectionalValuePipeStructuredTests
{
    private static void CheckCil(string source, long expected, string label)
    {
        var module = OresStructuredCfgLowering.LowerModule(source);
        string root = Path.Combine(Path.GetTempPath(), "atto-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string output = Path.Combine(root, "OresGenerated.dll");
            CilEmitter.EmitLibrary(module, output);
            using var assembly = AssemblyDefinition.ReadAssembly(output);
            var entry = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            long actual = CfgCilTestInterpreter.Evaluate(entry);
            if (actual != expected)
                throw new Exception($"{label}: compiled CIL returned {actual}, expected {expected}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RejectStructured(string source, string expected)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException error)
        {
            if (!error.Message.Contains(expected, StringComparison.Ordinal) ||
                !error.Message.Contains("offset", StringComparison.Ordinal))
                throw new Exception("Incorrect pipeline diagnostic: " + error.Message);
            return;
        }
        throw new Exception("Unsupported pipeline reached structured compiler admission");
    }

    public static void Run()
    {
        const string funcs =
            "fnc increment(int x):int{return x+1;}" +
            "fnc twice(int x):int{return x*2;}";

        CheckCil(funcs + "fnc main():int{return 20 |> increment |> twice;}",
            42, "forward two unary stages");
        CheckCil(funcs + "fnc main():int{return twice <| increment <| 20;}",
            42, "reverse two unary stages");
        CheckCil(funcs + "fnc main():int{return twice <| (20 |> increment);}",
            42, "explicitly grouped mixed application");
        CheckCil(funcs + "fnc main():int{return (twice <| increment <| 20);}",
            42, "grouped reverse expression");
        CheckCil("fnc identity(int x):int{return x;}fnc main():int{return identity <| 7;}",
            7, "one reverse stage");

        RejectStructured(funcs + "fnc main():int{return twice <| 20 |> increment;}",
            "E_VALUE_PIPE_DIRECTION");
        RejectStructured(funcs + "fnc main():int{return 20 |> increment <| twice;}",
            "E_VALUE_PIPE_DIRECTION");
        RejectStructured(funcs + "fnc main():int{return missing <| 20;}",
            "E_VALUE_PIPE_STAGE");
        RejectStructured(funcs + "fnc main():int{return twice <| true;}",
            "input type mismatch");
        RejectStructured("fnc sum(int a,int b):int{return a+b;}fnc main():int{return sum <| 20;}",
            "E_VALUE_PIPE_ARITY");
        RejectStructured(funcs + "fnc main():int{return 20 rpipe increment;}",
            "E_ACTOR_PIPE_BACKEND_UNSUPPORTED");
        RejectStructured(funcs + "fnc main():int{return 20 pipe increment;}",
            "E_ACTOR_PIPE_BACKEND_UNSUPPORTED");

        // The shipping one-main i64 compiler does not support named helper
        // definitions or tagged pipelines and must remain fail-closed.
        foreach (string op in new[] { "|>", "<|" })
        {
            try { _ = OresParser.Parse("fnc main():int{return 20 " + op + " increment;}"); }
            catch (FormatException failure) when
                (failure.Message.Contains("pipeline requires named callable lowering",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            throw new Exception("Shipping scalar frontend incorrectly admitted " + op);
        }
        _ = OresStructuredParser.Parse(
            "fnc main():int{let int rpipe=41;return rpipe+1;}");
        _ = OresStructuredParser.Parse(
            "fnc main():int{return 42;} // rpipe <| |> are inert here");
    }
}
