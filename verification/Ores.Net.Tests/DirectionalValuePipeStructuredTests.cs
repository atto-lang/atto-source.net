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
        // Same five SHA-identical source fixtures as Java and LLVM. Keep
        // parser admission, emitted CFG/CIL and native guest evidence separate.
        string corpus = Path.Combine(AppContext.BaseDirectory, "directional-parity");
        string Read(string filename) => File.ReadAllText(Path.Combine(corpus, filename));
        CheckCil(Read("directional-value-forward-v1.atto"), 42,
            "shared forward .atto guest CIL");
        CheckCil(Read("directional-value-reverse-v1.atto"), 42,
            "shared reverse .atto guest CIL");
        CheckCil(Read("directional-rpipe-contextual-name.atto"), 42,
            "shared contextual rpipe local CIL");
        RejectStructured(Read("directional-value-mixed-reject.atto"),
            "E_VALUE_PIPE_DIRECTION");
        RejectStructured(Read("directional-actor-rpipe-reject.atto"),
            "E_ACTOR_PIPE_BACKEND_UNSUPPORTED");

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

        CheckCil(funcs + "fnc main():int{return 41 |> await |> increment;}",
            42, "immediate await stage");
        CheckCil(funcs + "fnc main():int{return increment(await 41);}",
            42, "prefix immediate await");
        CheckCil(funcs + "fnc main():int{return 2 |> match<int> over { on 2 -> 41, on _ -> 0, } |> increment;}",
            42, "brace match receiver");
        CheckCil(funcs + "fnc main():int{return 2 |> match over on 2 -> 41, on _ -> 0, end |> increment;}",
            42, "end match receiver");
        CheckCil(funcs + "fnc main():int{return 2 |> await |> match over { on 2 -> 41, on _ -> 0, } |> increment;}",
            42, "await and match receiver composition");
        RejectStructured("fnc main():int{return true |> await;}", "immediate await requires int");
        RejectStructured("fnc main():int{return 42 |> await(7);}", "await takes only the left-hand value");
        RejectStructured("fnc main():int{return 42 |> std.await;}", "E_SUSPENDS_UNSUPPORTED");
        RejectStructured("fnc main():int{return 2 |> match over { on 2 -> 42, };}", "MATCH_NON_EXHAUSTIVE");

        foreach (string expr in new[] {
            "42 |> if is int then 30 else 20 fi",
            "42 |> is type int ? 30 : 20",
            "42 |> > 10 ? 30 : 20",
            "true |> ? 30 : 20",
            "true |> if then 30 else 20 fi",
            "false |> ifnot then 30 else 20 fi",
            "true |> if then 30 else 1/0 fi"
        })
            CheckCil(funcs + "fnc main():int{return " + expr + ";}", 30, expr);
        CheckCil(funcs + "fnc main():int{return 42 |> await |> if > 10 then 30 else 20 fi |> increment |> > 30 |> ? 100 : 200;}",
            100, "conditional receiver chain");
        CheckCil(funcs + "fnc main():int{return true |> ? 30 : 20 |> increment;}",
            31, "ternary receiver continuation");
        RejectStructured("fnc main():int{return 42 |> if then 30 else 20 fi;}", "Value if condition requires bool");
        RejectStructured("fnc main():int{return true |> is int ? 30 : 20;}", "Receiver int type test requires proven int");
        RejectStructured("fnc main():int{return true |> ? 30 : false;}", "Conditional result types differ");

        CheckCil("fnc main():int{return 42 |> match over { on 42 -> 100; is int -> -20; };}",
            100, "literal before type pattern");
        CheckCil("fnc main():int{return 7 |> await |> match over { on 42 -> 100; is int -> -20; };}",
            -20, "bare type pattern after await");
        CheckCil("fnc main():int{return match 7 over { on 42 -> 100; on is int -> -20; };}",
            -20, "legacy type arm equivalent");
        CheckCil("fnc main():int{return true |> match over { is bool -> 42; };}",
            42, "statically proven bool type pattern");
        RejectStructured("fnc main():int{return 7 |> match over { 7 -> 42; on _ -> 0; };}", "MATCH_SYNTAX");
        RejectStructured("fnc main():int{return 7 |> match over { _ -> 0; };}", "MATCH_SYNTAX");
        RejectStructured("fnc main():int{return 7 |> match over { is bool -> 42; on _ -> 0; };}", "MATCH_PATTERN_TYPE");
        RejectStructured("fnc main():int{return 7 |> match over { is int -> 42; on _ -> 0; };}", "MATCH_UNREACHABLE_ARM");

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
