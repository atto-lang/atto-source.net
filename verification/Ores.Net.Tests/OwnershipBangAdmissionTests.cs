using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// Real .NET frontend admission, NOT typed ownership lowering or Native AOT guest execution.
internal static class OwnershipBangAdmissionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        foreach (var operand in new[] {
            "copy!42", "copy! 42", "copy!(40 + 2)",
            "take!42", "take! 42",
            "borrow!value", "borrow! value",
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
            "pub fnc main(): int { // borrow!item\n return 42; }"
        })
        {
            if (OresParser.Parse(inert) is not Literal { Value: 42 })
                throw new Exception("Comment changed scalar source admission");
            if (OresStructuredParser.Parse(inert).Body.Count == 0)
                throw new Exception("Comment changed structured source admission");
        }

        foreach (string text in new[] { "copy !42", "borrow !42", "mut borrow !42", "copy!=42" })
        {
            string source = "pub fnc main(): int { return " + text + "; }";
            MustNotBeOwnershipAlias(() => OresParser.Parse(source), "scalar", text);
            MustNotBeOwnershipAlias(() => OresStructuredParser.Parse(source), "structured", text);
        }
        Console.WriteLine("NET_OWNERSHIP_BANG_FAIL_CLOSED_PASS frontend checks; NATIVE_AOT_NOT_EXECUTED");
    }

    private static void RequireUnsupported(Action action, string frontend, string input)
    {
        try { action(); }
        catch (FormatException ex) when (ex.Message.Contains(
            "OWNERSHIP_BANG_BACKEND_UNSUPPORTED", StringComparison.Ordinal)) { return; }
        throw new Exception("Missing typed ownership rejection: " + frontend + " " + input);
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
