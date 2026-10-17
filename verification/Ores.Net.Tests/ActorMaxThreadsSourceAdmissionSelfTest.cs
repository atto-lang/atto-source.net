using Ores.Net.Compiler;

internal static class ActorMaxThreadsSourceAdmissionSelfTest
{
    public static void Run()
    {
        static void Reject(string source, string diagnostic)
        {
            try { _ = OresParser.Parse(source); }
            catch (FormatException failure) when
                (failure.Message.Contains(diagnostic, StringComparison.Ordinal)) { return; }
            throw new Exception("ActorMaxThreads source must fail closed: " + source);
        }

        foreach (var tier in new[] { "LOW", "MEDIUM", "HIGH" })
        {
            Reject($"@ActorMaxThreads(MaxActorThreads.{tier}) define actor HungryActor as end",
                "source actor @ActorMaxThreads is unsupported");
        }
        Reject("@ActorMaxThreads(3) define actor A as end",
            "malformed @ActorMaxThreads");
        Reject("@ActorMaxThreads(MaxActorThreads.EXTREME) define actor A as end",
            "malformed @ActorMaxThreads");
        Reject("define enum MaxActorThreads as LOW int = 1 MEDIUM int = 2 HIGH int = 3 end "
                + "define actor HungryActor as end", "enum definitions require typed actor/enum lowering");
        Reject("define actor Worker as end", "source actors require typed actor scheduler lowering");
        if (OresParser.Parse("fnc main():int{return 42;} // "
                    + "@ActorMaxThreads(MaxActorThreads.HIGH)") is not Literal { Value: 42 })
            throw new Exception("commented actor annotation cannot change scalar semantics");
        Console.WriteLine("PASS .NET source actor max threads rejected until real lowering");
    }
}
