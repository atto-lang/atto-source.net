using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// Compiler-parser admission only; does not claim native AOT actor/model support.
internal static class DeclarationKindAdmissionSelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        string[] rejected =
        {
            "define actor Worker as\nend\n",
            "define supervisor Root as\nend\n",
            "define supervisor Root extends Base as\nend\n",
            "define grain User as\nend\n",
            "define grain User extends ActorBase as\nend\n",
            "define state container Counter as\nend\n",
            "define state container Counter implements Readable as\nend\n",
            "define model Network as\nend\n",
            "define enum Mode as\nend\n",
            "define class Box as\nend\n",
            "define struct Point as\nend\n",
            "define record Pair as\nend\n",
            "define trait Readable as\nend\n",
            "define interface Reader as\nend\n",
            "define protocol Wire as\nend\n",
            "define service Api as\nend\n",
        };

        foreach (string source in rejected)
        {
            bool denied = false;
            try { _ = OresParser.Parse(source); }
            catch (FormatException exception)
            {
                denied = !string.IsNullOrWhiteSpace(exception.Message);
            }
            if (!denied)
                throw new InvalidOperationException(
                    "Nominal kind escaped .NET scalar compiler admission: " + source);
        }
        // Positive control: a parser that rejects everything is not a valid gate.
        _ = OresParser.Parse("define fnc main():int{return 7;}\n");
        Console.WriteLine(".NET nominal declaration gate: 16 denials + scalar positive; no Native AOT claim");
    }
}
