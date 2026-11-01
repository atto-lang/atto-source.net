using Ores.Net.Compiler;

internal static class CriticalSuspendsBoundaryTests
{
    public static void Run()
    {
        foreach (var (source, diagnostic) in new (string, string)[] {
            ("async fnc main():int{return 42;}", "E_CALLABLE_MODIFIERS"),
            ("critical suspends fnc main():int{return 42;}", "E_CALLABLE_MODIFIERS"),
            ("suspends critical fnc main():int{return 42;}", "E_CALLABLE_MODIFIERS"),
            ("critical fnc main():int{return 42;}", "E_CRITICAL_UNSUPPORTED"),
            ("suspends fnc main():int{return 42;}", "E_SUSPENDS_UNSUPPORTED"),
            ("fnc main():int{ critical {return 42;} }", "E_CRITICAL_UNSUPPORTED"),
            ("fnc main():int{ try critical {return 42;} else {return 0;} }", "E_CRITICAL_UNSUPPORTED"),
            ("fnc main():int{return await 42;}", "E_SUSPENDS_UNSUPPORTED"),
            ("fnc main():int{return critical {42};}", "E_CRITICAL_UNSUPPORTED"),
            ("fnc main():int{return 42;} /* critical /* suspends */ async */", "ACCEPT")
        })
        {
            if (diagnostic == "ACCEPT")
            {
                if (OresParser.Parse(source) is not Literal { Value: 42 })
                    throw new Exception("Comment containing critical modifiers changed scalar parsing");
                continue;
            }
            try
            {
                _ = OresParser.Parse(source);
            }
            catch (FormatException error) when (error.Message.Contains(diagnostic, StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception($"Source was not rejected with {diagnostic}: {source}");
        }
        if (OresParser.Parse("fnc main():int{return 42;} // critical suspends await") is not Literal { Value: 42 })
            throw new Exception("Line comment must not trigger critical effect rejection");
    }
}
