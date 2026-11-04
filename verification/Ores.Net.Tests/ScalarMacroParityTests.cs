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
    private const string TwiceMacro = """
        define macro twice(Syntax<Expr> value) -> Syntax<Expr> {
            return quote(unquote(value) + unquote(value));
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
        string twiceSource = TwiceMacro + Main("twice!(syntax expr 21)");
        string twiceNestedSource = LiteralMacro + TwiceMacro +
            Main("twice!(syntax expr literal!(const 20 + 1))");
        Require(OresParser.Parse(constSource) is Literal { Value: 42 },
            "Const<int> quote must lower to a literal AST node");
        Require(OresParser.Parse(identitySource) is Literal { Value: 42 },
            "Syntax<Expr> identity must preserve the call-site AST node");
        Expect42(constSource, "Const<int> quote must survive OresIR verification and interpretation");
        Expect42(identitySource, "Syntax<Expr> identity must survive OresIR");
        // Repeated splice creates two separately owned compiler AST leaves,
        // and normal OresIR verification handles the generated binary Add.
        Expression twiceTree = OresParser.Parse(twiceSource);
        Require(twiceTree is Binary { Operator: '+' } plus &&
            plus.Left is Literal { Value: 21 } &&
            plus.Right is Literal { Value: 21 } &&
            !ReferenceEquals(plus.Left, plus.Right),
            "typed quote/unquote emits distinct AST identities");
        Expect42(twiceSource, "twice! literal template re-enters checked OresIR");
        Expect42(twiceNestedSource, "nested literal! inside twice! expands before OresIR");
        Expect42(LiteralMacro + Main("literal!(const (3 * 4) + (7 * 6) - 12)"),
            "mixed checked multiply/add/subtract constant must evaluate to 42");
        Expect42(IdentityMacro + Main("identity!(syntax expr (6 * 7))"),
            "non-literal identity syntax retains expression semantics");
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
            var verified = OresIrLowering.Lower(expanded);
            CilEmitter.EmitLibrary(verified, a);
            CilEmitter.EmitLibrary(verified, b);
            Require(File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)),
                "macro CIL output must be deterministic");
            using var assembly = AssemblyDefinition.ReadAssembly(a);
            var entry = assembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            Require(CilReferenceEvaluator.Evaluate(entry) == 42,
                "generated CIL from the macro AST must evaluate to 42");

            // The structural template must pass the same verified OresIR ->
            // deterministic emitted CIL path, not a reference-AST shortcut.
            string t1 = Path.Combine(folder, "macro.twice.a.dll");
            string t2 = Path.Combine(folder, "macro.twice.b.dll");
            var templateIr = OresIrLowering.Lower(OresParser.Parse(twiceNestedSource));
            CilEmitter.EmitLibrary(templateIr, t1);
            CilEmitter.EmitLibrary(templateIr, t2);
            Require(File.ReadAllBytes(t1).AsSpan().SequenceEqual(File.ReadAllBytes(t2)),
                "nested typed template CIL bytes must be deterministic");
            using var templateAssembly = AssemblyDefinition.ReadAssembly(t1);
            var templateEntry = templateAssembly.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(m => m.Name == "Run");
            Require(CilReferenceEvaluator.Evaluate(templateEntry) == 42,
                "typed template generated CIL evaluates to 42");
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
        Reject("define macro literal(Const<int> quote) -> Syntax<Expr> { return quote(quote); }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "quote must not be rebound in macro scope");
        Reject("define macro identity(Syntax<Expr> unquote) -> Syntax<Expr> { return unquote; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "unquote must not be rebound in macro scope");
        Reject("define macro identity(Syntax<Expr> x) -> Syntax<Expr> { return quote(x+x); }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "generated expression/call-site duplication");
        Reject("define macro invalid(Const<int> x) -> Syntax<Decl> { return quote(x); }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "result-kind mismatch rejected at declaration");
        Reject("define macro bad(Syntax<Expr> x) -> Syntax<Expr> { return dynamic_call(x); }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "unsupported macro helper body");
        Reject("define macro bad(Syntax<Expr> x, Syntax<Expr> y) -> Syntax<Expr> { return x; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "multi-parameter expander unavailable");
        Reject("define macro syntax(Syntax<Expr> x) -> Syntax<Expr> { return x; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "syntax tag is reserved, even for unused macros");
        Reject("define macro identity(Syntax<Expr> const) -> Syntax<Expr> { return const; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "const tag must not be rebound as a macro parameter");
        Reject("define macro rt(Syntax<Expr> x) -> Syntax<Expr> { return x; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "runtime ownership prefix must not name a macro");
        Reject("define macro identity(Syntax<Expr> Expr) -> Syntax<Expr> { return Expr; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "typed syntax names must not be macro parameters");
        Reject("define macro eval(Syntax<Expr> x) -> Syntax<Expr> { return x; }" +
            Main("42"), "NET_MACRO_EXPANDER_REQUIRED", "eval intrinsic must not be rebound");
        Reject("define macro unsafe(Syntax<Expr> value) -> Syntax<Expr> {" +
            "return quote(unquote(value) + unquote(other)); }" + Main("42"),
            "NET_MACRO_EXPANDER_REQUIRED", "unused invalid template declaration must fail before CIL");
        Reject(LiteralMacro + Main("literal!(const -(-9223372036854775808))"),
            "NET_MACRO_CONST_OVERFLOW", "shared unary minimum signed overflow");
        Reject(TwiceMacro + Main("twice!(syntax expr 20 + 1)"),
            "NET_MACRO_EXPANDER_REQUIRED", "template must not duplicate a composite expression");
        Reject(TwiceMacro + Main("twice!(syntax expr runtime_value)"),
            "NET_MACRO_EXPANDER_REQUIRED", "template must not duplicate a runtime identifier");
        Reject(TwiceMacro + Main("twice!(syntax expr 9223372036854775807)"),
            "NET_MACRO_CONST_OVERFLOW", "repeated positive literal overflow");
        Reject(TwiceMacro + Main("twice!(syntax expr -9223372036854775808)"),
            "NET_MACRO_CONST_OVERFLOW", "repeated negative minimum literal overflow");
        Reject("define macro twice(Syntax<Expr> x) -> Syntax<Expr> {" +
            "return quote(unquote(x) + unquote(y)); }" + Main("42"),
            "NET_MACRO_EXPANDER_REQUIRED", "template cannot splice undeclared parameter");
        Reject("define macro twice(Syntax<Expr> x) -> Syntax<Expr> {" +
            "return quote(unquote(x) + x); }" + Main("42"),
            "NET_MACRO_EXPANDER_REQUIRED", "template requires two direct unquotes");
        Reject("define macro twice(Syntax<Expr> x) -> Syntax<Expr> {" +
            "return quote(unquote(x) + unquote(x) + unquote(x)); }" + Main("42"),
            "NET_MACRO_EXPANDER_REQUIRED", "template cannot amplify syntax three times");
        Reject(TwiceMacro + Main("twice!(const 21)"),
            "NET_MACRO_EXPANDER_REQUIRED", "template requires Syntax<Expr>, not Const<int>");
        RejectStructured(TwiceMacro + Main("twice!(syntax expr 21)"),
            "structured typed template must fail closed");
        Reject(LiteralMacro + LiteralMacro + Main("42"),
            "NET_MACRO_EXPANDER_REQUIRED", "duplicate macro must fail even if unused");
        Reject(IdentityMacro + Main("identity!(const 42)"),
            "NET_MACRO_EXPANDER_REQUIRED", "argument category must be Syntax<Expr>");
        Reject(LiteralMacro + Main("literal!(syntax expr 42)"),
            "NET_MACRO_EXPANDER_REQUIRED", "argument category must be Const<int>");
        Reject(Main("missing!(syntax expr 42)"),
            "NET_MACRO_EXPANDER_REQUIRED", "undefined macros do not become runtime calls");
        // Reserved intrinsic spellings are *not* valid macro declarations,
        // but an attempted bang call must still take the fail-closed compiler
        // macro path rather than an ordinary scalar value/call fallback.
        foreach (string spelling in new[] { "quote", "unquote", "eval", "syntax", "const",
                                             "rt", "main", "Expr", "Syntax", "Const" })
        {
            Reject(Main(spelling + "!(syntax expr 42)"),
                "NET_MACRO_EXPANDER_REQUIRED",
                "reserved macro-like bang invocation " + spelling);
        }
        Reject(Main("42!(syntax expr 42)"),
            "NET_MACRO_EXPANDER_REQUIRED",
            "non-identifier bang invocation cannot bypass compile-time admission");
        Reject(LiteralMacro + Main("literal!(const dynamic_name)"),
            "Expected integer", "dynamic Const<int> must fail before CIL");
        Reject(LiteralMacro + Main("literal!(const 9223372036854775807 + 1)"),
            "NET_MACRO_CONST_OVERFLOW", "checked constant overflow");
        Reject(LiteralMacro + Main("literal!(const -(-9223372036854775808))"),
            "NET_MACRO_CONST_OVERFLOW", "unary signed overflow");
        Reject(LiteralMacro + Main("literal!(const 1 / 2)"),
            "Division requires float result lowering", "integer division cannot change language typing");

        // Inclusive 64-level const evaluator bound, distinct from parser's
        // independent 128 nesting bound. No runtime bypass on depth 65.
        string negations64 = string.Concat(Enumerable.Repeat("-", 64)) + "1";
        string negations65 = "-" + negations64;
        Require(OresParser.Parse(LiteralMacro + Main($"literal!(const {negations64})")) is
            Literal { Value: 1 }, "64 nested unary const nodes are admitted");
        Reject(LiteralMacro + Main($"literal!(const {negations65})"),
            "NET_MACRO_CONST_BUDGET", "65 nested const nodes must fail closed");

        // Macro-expansion count boundary, independent of AST evaluator budget.
        string invocations128 = string.Join("+",
            Enumerable.Repeat("identity!(syntax expr 1)", 128));
        string invocations129 = invocations128 + "+identity!(syntax expr 1)";
        Require(OresParser.Parse(IdentityMacro + Main(invocations128)) is Binary,
            "128 macro expansions are admitted in closed scalar frontend");
        Reject(IdentityMacro + Main(invocations129),
            "NET_MACRO_EXPANDER_REQUIRED", "129th expansion must be rejected");

        string broad = "1";
        for (int i = 0; i < 8; ++i) broad = "(" + broad + "+" + broad + ")";
        Require(OresParser.Parse(LiteralMacro + Main($"literal!(const {broad})")) is
            Literal { Value: 256 }, "511-node Const<int> AST stays within 512 visits");
        broad = "(" + broad + "+" + broad + ")";
        Reject(LiteralMacro + Main($"literal!(const {broad})"),
            "NET_MACRO_CONST_BUDGET", "1023-node shallow AST exceeds 512 evaluator visits");

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
