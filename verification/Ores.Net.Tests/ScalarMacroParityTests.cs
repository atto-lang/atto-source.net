using Ores.Net.Compiler;
using Mono.Cecil;

public static class ScalarMacroParityTests
{
    private const string LiteralMacro = """
        define macro literal(Const<int> value) -> Syntax<Expr> {
            return quote(value);
        }
        """;
    private const string IdentityMacro = """
        define macro identity(Syntax<Expr> value) -> Syntax<Expr> {
            return value;
        }
        """;

    private static string Main(string expression) =>
        $"pub fnc main(): int {{ return {expression}; }}";

    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("NET_MACRO_PARITY FAILED: " + why);
    }

    private static void Reject(string source, string diagnostic, string why)
    {
        try { _ = OresParser.Parse(source); }
        catch (FormatException e) when (e.Message.Contains(diagnostic, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Expected fail-closed .NET macro rejection: " + why);
    }

    private static void RejectStructured(string source, string why)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException e) when (
            e.Message.Contains("NET_STRUCTURED_MACRO_EXPANDER_REQUIRED", StringComparison.Ordinal) &&
            e.Message.Contains("runtime fallback is forbidden", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Expected structured .NET macro rejection: " + why);
    }

    private static void Expect42(string source, string why)
    {
        Expression expression = OresParser.Parse(source);
        var ir = OresIrLowering.Lower(expression);
        Require(OresIrReferenceEvaluator.Evaluate(ir) == 42, why);
    }

    public static void Run()
    {
        string constSource = LiteralMacro + Main("literal!(const 40 + 2)");
        string identitySource = IdentityMacro + Main("identity!(syntax expr 42)");
        Require(OresParser.Parse(constSource) is Literal { Value: 42 },
            "Const<int> quote must lower to a literal AST node");
        Require(OresParser.Parse(identitySource) is Literal { Value: 42 },
            "Syntax<Expr> identity must preserve the call-site AST node");
        Expect42(constSource, "Const<int> quote must survive OresIR verification and interpretation");
        Expect42(identitySource, "Syntax<Expr> identity must survive OresIR");
        Expect42(LiteralMacro + IdentityMacro +
            Main("identity!(syntax expr literal!(const 6 * 7))"),
            "nested macros must expand completely before OresIR");
        Expect42(IdentityMacro + Main("identity!(syntax expr 21 + 21)"),
            "identity must splice an arithmetic AST without textual rewriting");
        Expect42(LiteralMacro + Main("literal!(const -9223372036854775808 + 9223372036854775807 + 43)"),
            "checked intermediate constants must admit valid signed-i64 arithmetic");

        // Verify the real source -> checked OresIR -> deterministic CIL path,
        // rather than only an in-memory expression evaluator. This metadata
        // check does not claim a published Native AOT executable.
        string folder = Path.Combine(Path.GetTempPath(),
            "oresnet-macro-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string a = Path.Combine(folder, "macro.a.dll");
            string b = Path.Combine(folder, "macro.b.dll");
            Expression expanded = OresParser.Parse(constSource);
            CilEmitter.EmitLibrary(expanded, a);
            CilEmitter.EmitLibrary(expanded, b);
            Require(File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)),
                "macro CIL output must be deterministic");
            using var assembly = AssemblyDefinition.ReadAssembly(a);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            Require(CilReferenceEvaluator.Evaluate(entry) == 42,
                "generated CIL from the macro AST must evaluate to 42");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        // Compiler-only declarations: unsupported/uninvoked ones are not discarded.
        Reject("define pub macro literal(Const<int> x) -> Syntax<Expr> { return quote(x); }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "public macro ABI requires export metadata");
        Reject("pub define macro literal(Const<int> x) -> Syntax<Expr> { return quote(x); }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "alternate public macro spelling");
        Reject("define macro make(Type x) -> Syntax<Expr> { return x; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "unsupported type input even when unused");
        Reject("define macro identity(Syntax<Expr> x) -> Syntax<Expr> { return quote(x+x); }" +
            Main("42"), "Expected 'x'", "generated expression/call-site duplication");
        Reject(LiteralMacro + LiteralMacro + Main("42"),
            "NET_MACRO_EXPANDER_REQUIRED", "duplicate macro must fail even if unused");
        Reject(IdentityMacro + Main("identity!(const 42)"),
            "NET_MACRO_EXPANDER_REQUIRED", "argument category must be Syntax<Expr>");
        Reject(LiteralMacro + Main("literal!(syntax expr 42)"),
            "NET_MACRO_EXPANDER_REQUIRED", "argument category must be Const<int>");
        Reject(Main("missing!(syntax expr 42)"),
            "NET_MACRO_EXPANDER_REQUIRED", "undefined macros do not become runtime calls");
        Reject(LiteralMacro + Main("literal!(const dynamic_name)"),
            "Expected integer", "dynamic Const<int> must fail before CIL");
        Reject(LiteralMacro + Main("literal!(const 9223372036854775807 + 1)"),
            "NET_MACRO_CONST_OVERFLOW", "checked constant overflow");
        Reject(LiteralMacro + Main("literal!(const -(-9223372036854775808))"),
            "NET_MACRO_CONST_OVERFLOW", "unary signed overflow");
        Reject(LiteralMacro + Main("literal!(const 1 / 2)"),
            "Division requires float result lowering", "integer division cannot change language typing");

        string broad = "1";
        for (int i = 0; i < 9; ++i) broad = "(" + broad + "+" + broad + ")";
        Reject(LiteralMacro + Main($"literal!(const {broad})"),
            "NET_MACRO_CONST_BUDGET", "broad shallow AST must respect evaluator budget");

        string many = string.Concat(Enumerable.Range(0, 9).Select(i =>
            $"define macro ident_{i}(Syntax<Expr> x) -> Syntax<Expr> {{ return x; }}\n"));
        Reject(many + Main("42"), "NET_MACRO_EXPANDER_REQUIRED",
            "more than eight macro declarations must fail closed");

        // No comment-delimiter, quoted source or textual preprocessing trick can
        // introduce an executable macro without a real compiler AST declaration.
        Require(OresParser.Parse(Main("42") + " // define macro fake") is Literal { Value: 42 },
            "line-comment decoy must be ignored");
        Require(OresParser.Parse(Main("42") +
            " /* define macro fake /* identity!(syntax expr 99) */ end */") is Literal { Value: 42 },
            "nested block-comment decoy must be ignored");

        // The experimental structured frontend has NO macro execution and must
        // reject its syntax itself (not pass it through to CFG/CIL or module import).
        RejectStructured(constSource, "structured macro declaration");
        RejectStructured(identitySource, "structured macro identity");
        RejectStructured(Main("identity!(syntax expr 42)"), "structured macro invocation");
        Require(OresStructuredParser.Parse(Main("42") +
            " // identity!(syntax expr 99)") is not null,
            "structured comment decoy must remain invisible");
    }
}
