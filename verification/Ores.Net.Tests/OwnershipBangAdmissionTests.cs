using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// Real .NET frontend admission, NOT typed ownership lowering or Native AOT guest execution.
internal static class OwnershipBangAdmissionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        foreach (var operand in new[] {
            "copy!42", "copy! 42", "copy!(40 + 2)", "copy!eqfoo",
            "take!42", "take! 42",
            "borrow!value", "borrow! value",
            "borrow! mut value", "borrow!mut value",
            "borrow!\nmut value", "borrow!/* checked loan */mut value",
            "mut borrow!\nvalue",
            "share!value", "share! value",
            "mut borrow!value", "mut borrow! value"
        })
        {
            string source = "pub fnc main(): int { return " + operand + "; }";
            RequireUnsupported(() => OresParser.Parse(source), "scalar", operand);
            RequireUnsupported(() => OresStructuredParser.Parse(source), "structured", operand);
        }

        foreach (string inert in new[] {
            "pub fnc main(): int { /* copy! 7 */ return 42; }",
            "pub fnc main(): int { // borrow!item\n return 42; }",
            "pub fnc main(): int { /* outer /* borrow! mut item */ " +
                "tail /* copy!42 */ */ return 42; }"
        })
        {
            if (OresParser.Parse(inert) is not Literal { Value: 42 })
                throw new Exception("Comment changed scalar source admission");
            if (OresStructuredParser.Parse(inert).Body.Count == 0)
                throw new Exception("Comment changed structured source admission");
        }

        foreach (string text in new[] { "copy !42", "borrow !42", "mut borrow !42",
                                          "copy!=42", "copy!eq 42", "borrow!eq 42" })
        {
            string source = "pub fnc main(): int { return " + text + "; }";
            MustNotBeOwnershipAlias(() => OresParser.Parse(source), "scalar", text);
            MustNotBeOwnershipAlias(() => OresStructuredParser.Parse(source), "structured", text);
        }
        // Nested comments are parsed by both source admission frontends;
        // ownership-looking text inside them cannot mint an unsafe operation.
        string deepComment = "pub fnc main(): int { " +
            string.Concat(Enumerable.Repeat("/*", 129)) + "copy!42" +
            string.Concat(Enumerable.Repeat("*/", 129)) + " return 42; }";
        RequireCommentLimit(() => OresParser.Parse(deepComment), "scalar");
        RequireCommentLimit(() => OresStructuredParser.Parse(deepComment), "structured");

        // Historical argument-order compatibility must reach the same
        // unsupported-ownership diagnostic, never an obsolete syntax error.
        const string legacy = "pub fnc main(): int { return rt borrow mut value; }";
        RequireLegacyTypedDenial(() => OresParser.Parse(legacy), "scalar");
        RequireLegacyTypedDenial(() => OresStructuredParser.Parse(legacy), "structured");
        Console.WriteLine("NET_OWNERSHIP_BANG_FAIL_CLOSED_PASS frontend checks; NATIVE_AOT_NOT_EXECUTED");
    }

    private static void RequireCommentLimit(Action action, string frontend)
    {
        try { action(); }
        catch (FormatException ex) when (ex.Message.Contains(
            "Block comment nesting exceeds 128", StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Unbounded nested comments in " + frontend);
    }

    private static void RequireUnsupported(Action action, string frontend, string input)
    {
        try { action(); }
        catch (FormatException ex) when (ex.Message.Contains(
            "OWNERSHIP_BANG_BACKEND_UNSUPPORTED", StringComparison.Ordinal)) { return; }
        throw new Exception("Missing typed ownership rejection: " + frontend + " " + input);
    }

    private static void RequireLegacyTypedDenial(Action action, string frontend)
    {
        try { action(); }
        catch (FormatException ex) when (ex.Message.Contains(
            "requires typed actor-arena lowering", StringComparison.Ordinal)) { return; }
        throw new Exception("Legacy mutable borrow unexpectedly bypassed typed gate: " + frontend);
    }

    private static void MustNotBeOwnershipAlias(Action action, string frontend, string input)
    {
        try { action(); }
        catch (FormatException ex)
        {
            if (ex.Message.Contains("OWNERSHIP_BANG_BACKEND_UNSUPPORTED", StringComparison.Ordinal))
                throw new Exception("Incorrect ownership token adjacency: " + frontend + " " + input);
        }
    }
}
