using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// These execute the actual two compiler parsers in the managed test process.
// Fail-closed admission is NOT positive virtual dispatch or NativeAOT parity.
internal static class PrivateAbstractActorAdmissionSelfTest
{
    private static void Denies(string source, string code)
    {
        Action<string>[] parsers =
        {
            text => { _ = OresParser.Parse(text); },
            text => { _ = OresStructuredParser.Parse(text); }
        };
        foreach (var parser in parsers)
        {
            try
            {
                parser(source);
            }
            catch (FormatException error) when (error.Message.Contains(code, StringComparison.Ordinal))
            {
                continue;
            }
            throw new InvalidOperationException(
                $"Private abstract actor boundary did not reject '{code}' in source: {source}");
        }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        // Required override, originating-slot provenance and inherited generic
        // substitution are not implemented in the .NET CIL/Native AOT compiler.
        foreach (string source in new[]
        {
            "define abstract class Base as private abstract mtd hook(): int; end",
            "pub define abstract class Base as private abstract mtd hook(): int; end",
            "define pub abstract class Base as private abstract mtd hook(): int; end"
        })
            Denies(source, "NET_PRIVATE_ABSTRACT_UNSUPPORTED");

        // Neither implicit nor explicit self can authorize actor entrypoint
        // dispatch. All paths remain closed until a compiler-owned mailbox ABI
        // and trusted runtime turn token are implemented. The two spellings
        // must be rejected consistently, not miscompiled into different arities.
        foreach (string source in new[]
        {
            "define actor Mailer as receive(ActorMail<int> mail): void { return; } end",
            "define actor Mailer as receive(self, ActorMail<int> mail): void { return; } end",
            "define actor Mailer as private abstract mtd receive(ActorMail<int> mail): void; end",
            "define actor Mailer as external mtd receive(ActorMail<int> mail): void { return; } end",
            "define actor Mailer as pub mtd receive(ActorMail<int> mail): void { return; } end",
            "define actor Mailer as mtd on_start(): void { return; } end",
            // Ordinary actor code must not invoke/capture trusted ingress,
            // including super and bound method references.
            "define actor Mailer as mtd run(): void { self.receive(1); } end",
            "define actor Mailer as mtd run(): void { super.receive(1); } end",
            "define actor Mailer as mtd run(): void { let ref = self.receive; } end",
            "define class Derived as mtd run(): int { return super.hook(); } end"
        })
            Denies(source, "NET_NOMINAL_METHODS_UNSUPPORTED");

        // A gate that rejects everything is not evidence. Also ensure that
        // comment content cannot forge unsupported declaration tokens.
        _ = OresParser.Parse("fnc main():int{return 42;} // private abstract actor receive(self)");
        _ = OresStructuredParser.Parse("fnc main():int{return 42;} // private abstract actor receive(self)");
        Console.WriteLine(".NET private abstract actor admission: 13 two-parser negative cases + scalar controls; no AOT/guest parity claim");
    }
}
