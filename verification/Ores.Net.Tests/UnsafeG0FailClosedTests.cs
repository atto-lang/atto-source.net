using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

// G0 only: source rejection, NOT verified FFI/unsafe effect or Native AOT support.
internal static class UnsafeG0FailClosedTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        foreach (var source in new[] {
            "unsafe fnc main():int{return 42;}",
            "fnc main():int{unsafe{return 42;}}",
            "fnc unsafe():int{return 42;}",
            "fnc main(unsafe:int):int{return 42;}",
            "fnc main():int{return 42;} unsafe",
            "fnc main():int{return std.unsafe.memory.read();}"
        })
        {
            foreach (var parse in new Action<string>[] {
                s => { _ = OresParser.Parse(s); },
                s => { _ = OresStructuredParser.Parse(s); }
            })
            {
                try
                {
                    parse(source);
                }
                catch (FormatException ex) when (
                    ex.Message.Contains("unsafe boundary requires checked effects",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                throw new Exception("unsafe syntax evaded explicit .NET source admission: " +
                                    source);
            }
        }
        foreach (var newline in new[] { "\r", "\r\n", "\n", "\u0085", "\u2028", "\u2029" })
        {
            var source = "fnc main():int{return 42;} // ignored" + newline + "unsafe";
            foreach (var parse in new Action<string>[] {
                x => { _ = OresParser.Parse(x); },
                x => { _ = OresStructuredParser.Parse(x); }
            })
            {
                try { parse(source); }
                catch (FormatException ex) when (
                    ex.Message.Contains("unsafe boundary requires checked effects",
                        StringComparison.Ordinal)) { continue; }
                throw new Exception("unsafe on a CR/LF line escaped the lexer");
            }
        }
        // This is a lexical keyword, not a substring check.
        foreach (var safe in new[] {
            "fnc main():int{return 42;} // unsafe",
            "fnc main():int{return 42;} /* unsafe /* unsafe */ */",
            "fnc main():int{return 42;} // unsafe_helper",
            "fnc main():int{return 42;} /* outer /* unsafe */ outer */"
        })
        {
            _ = OresParser.Parse(safe);
            _ = OresStructuredParser.Parse(safe);
        }
    }
}
