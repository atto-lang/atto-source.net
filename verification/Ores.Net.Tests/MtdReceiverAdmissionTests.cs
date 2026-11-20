using Ores.Net.Compiler;

/// <summary>
/// Safety gate only. Actual class receiver lowering must be added before
/// anyone may claim .NET Native AOT method parity with Java or LLVM.
/// </summary>
internal static class MtdReceiverAdmissionTests
{
    private const string Diagnostic = "MTD_RECEIVER_NATIVEAOT_UNSUPPORTED";

    private static void Reject(string source)
    {
        foreach (var parse in new Func<string, object>[] {
            s => OresStructuredParser.Parse(s),
            s => OresParser.Parse(s)
        })
        {
            try { _ = parse(source); }
            catch (FormatException ex) when (
                ex.Message.Contains(Diagnostic, StringComparison.Ordinal) &&
                ex.Message.Contains("offset", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception(
                "Receiver-first mtd reached an unsupported .NET compiler path without a typed CIL gate");
        }
    }

    public static void Run()
    {
        foreach (var fixture in new[] { "typed-read.atto", "typed-mutable.atto" })
        {
            string path = Path.Combine(AppContext.BaseDirectory,
                "contracts", "mtd-receiver-v1", fixture);
            Reject(File.ReadAllText(path));
        }
        Reject("mtd (Point self) foo(): void { return; }");
        Reject("mtd (Point mut self) mutate(): void { return; }");
        Reject("mtd (Foo1 self) foo(): void { return; }");
        Reject("mtd (Foo2 self) foo(): void { return; }");
        Reject("mtd (self: Point) read_x(): int { return self.x; }");
        Reject("mtd (mut self: Point) move_by(int dx): void { self.x += dx; }");
        Reject("pub mtd (mut self: Point) move_by(int dx): void { return; }");
        Reject("mtd /*comment*/ (mut /*comment*/ self: Point) read(): int { return 1; }");

        // The token-aware admission must not treat a mere identifier or
        // comment as a receiver declaration.
        const string ordinary = "fnc main():int{/* mtd (mut self: Point) */ return 42;}";
        if (OresStructuredParser.Parse(ordinary).Body.Count != 1 ||
            OresParser.Parse(ordinary) is null)
            throw new Exception("mtd comment incorrectly enabled a receiver feature gate");

        // Contextual 'mtd' after fnc is a callable name, not a declaration
        // marker. The scalar frontend may reject name-first parameters for
        // other reasons, but must never misreport external-method support.
        foreach (var parse in new Func<string, object>[] {
            source => OresStructuredParser.Parse(source),
            source => OresParser.Parse(source)
        })
        {
            try { _ = parse("fnc mtd(self: int): int { return self; }"); }
            catch (FormatException ex)
            {
                if (ex.Message.Contains(Diagnostic, StringComparison.Ordinal))
                    throw new Exception("ordinary function named mtd misclassified");
            }
        }

        var helper = OresStructuredParser.Parse(
            "fnc mtd(int value):int{return value;} fnc main():int{return mtd(42);}");
        if (helper.Body.Count == 0)
            throw new Exception("ordinary method-name identifier mtd was incorrectly reserved");
    }
}
