using System.Runtime.CompilerServices;
using Ores.Net.Compiler;

internal static class CriticalSuspendsAdmissionV1Tests
{
    [ModuleInitializer]
    internal static void Run()
    {
        static void Reject(string source, string code)
        {
            try { _ = OresParser.Parse(source); }
            catch (FormatException error) when (
                error.Message.Contains(code, StringComparison.Ordinal)) { return; }
            throw new Exception("Critical/suspends bootstrap admitted unsupported source or wrong diagnostic: " + code);
        }

        Reject("critical fnc main():int{return 1;}", "E_CRITICAL_UNLOWERED");
        Reject("fnc main():int{critical{return 1;}}", "E_CRITICAL_UNLOWERED");
        Reject("fnc main():int{critical gate{return 1;}}", "E_CRITICAL_UNLOWERED");
        Reject("fnc main():int{try critical{return 1;}else{return 2;}}",
            "E_CRITICAL_UNLOWERED");
        Reject("suspends fnc main():int{return 1;}", "E_SUSPENDS_UNLOWERED");
        Reject("suspends critical fnc main():int{return 1;}", "E_CRITICAL_SUSPENDS_DECL");
        Reject("critical suspends fnc main():int{return 1;}", "E_CRITICAL_SUSPENDS_DECL");
        Reject("pub critical /* nested /* block */ test */ suspends fnc main():int{return 1;}", "E_CRITICAL_SUSPENDS_DECL");
        Reject("pub suspends // comment\n critical fnc main():int{return 1;}", "E_CRITICAL_SUSPENDS_DECL");
        Reject("async fnc main():int{return 1;}", "E_ASYNC_FUNCTION_REMOVED");
        Reject("async routine main():int{return 1;}", "E_ASYNC_FUNCTION_REMOVED");
        Reject("async generator fnc values():int{yield 1;}", "E_ASYNC_UNLOWERED");
        // Genuine scalar parsing is unaffected by words inside comments.
        if (OresParser.Parse("fnc main():int{return 7;} // critical suspends async") is not Literal)
            throw new Exception("comment words changed critical lexer admission");
        if (OresParser.Parse("fnc main():int{return 7;} /* critical /* async */ suspends */") is not Literal)
            throw new Exception("nested comment words changed critical lexer admission");
    }
}
