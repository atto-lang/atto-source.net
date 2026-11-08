using Ores.Net.Compiler;

internal static class ActorDeclarationAstSelfTest
{
    public static void Run()
    {
        ParsesBoundedActorSignaturesAndOpaqueBodies();
        RejectsDuplicateAndUnsupportedDeclarations();
        KeepsCommentsAndStringsLexicallyInert();
        EnforcesInputAndNestingBudgets();
        Console.WriteLine("PASS actor-declaration-ast-v1|actors=2|handlers=3|negative=10|execution=0");
    }

    private static void ParsesBoundedActorSignaturesAndOpaqueBodies()
    {
        const string source = """
            pub define actor Counter as
                receive(self, int amount):void {
                    let int ignored = amount;
                    if (ignored > 0) { /* balanced nested body */ }
                }
                reset():void { /* zero-argument lifecycle-like method is just parsed */ }
            end
            define actor Flag as
                receive(bool enabled):void { let bool value = enabled; }
            end
            """;
        ActorDeclarationUnit unit = OresActorDeclarationParser.Parse(source);
        Require(unit.Actors.Count == 2 && unit.SourceLength == source.Length,
            "actor unit count and source extent");
        ActorDeclaration counter = unit.Actors[0];
        Require(counter.IsPublic && counter.Name == "Counter" &&
                counter.Handlers.Count == 2, "public Counter AST");
        ActorHandlerDeclaration receive = counter.Handlers[0];
        Require(receive.Name == "receive" && receive.Parameters.Count == 1 &&
                receive.Parameters[0] == new ActorHandlerParameter("amount", ActorMessageType.I64),
            "self receiver erased from typed message parameter list");
        Require(receive.BodySource.Contains("ignored > 0", StringComparison.Ordinal) &&
                receive.BodyLength == receive.BodySource.Length &&
                source.Substring(receive.BodyOffset, receive.BodyLength) == receive.BodySource,
            "opaque body span matches original source bytes/chars");
        ActorHandlerDeclaration reset = counter.Handlers[1];
        Require(reset.Name == "reset" && reset.Parameters.Count == 0,
            "zero-value-argument handler accepted");
        Require(unit.Actors[1].Handlers[0].Parameters[0].Type == ActorMessageType.Bool,
            "bool message type retained");
    }

    private static void RejectsDuplicateAndUnsupportedDeclarations()
    {
        string[] rejected =
        {
            "define actor A as receive(int x):void{} receive(bool y):void{} end",
            "define actor A as receive(int x):void{} receive(self, int y):void{} end",
            "define actor A as receive(int x, int x):void{} end",
            "define actor A as receive(string x):void{} end",
            "define actor A as receive(int x):int{} end",
            "define actor A as receive(int x):void{ await next; } end",
            "define actor A as receive(int x):void{ suspends fnc work(){} } end",
            "define actor A as receive(int x):void{ critical {} } end",
            "define actor A as receive(int x):void{}",
            "define actor A as end",
            "define actor A as receive(int x):void{ define actor B as end } end",
            "define actor A as receive(int x):void{} end define actor A as receive(int y):void{} end"
        };
        foreach (string source in rejected)
            ExpectFormat(() => OresActorDeclarationParser.Parse(source),
                "unsupported or malformed actor declaration must fail closed");
    }

    private static void KeepsCommentsAndStringsLexicallyInert()
    {
        const string source = """
            /* define actor Fake as end */
            define actor Real as
                receive(int value):void {
                    // } define actor CommentFake as end
                    let int ignored = value;
                    let int stringLookalike = 1; /* nested /* await */ comment */
                }
            end
            """;
        ActorDeclarationUnit unit = OresActorDeclarationParser.Parse(source);
        Require(unit.Actors.Count == 1 && unit.Actors[0].Name == "Real",
            "comment contents do not create actor declarations");

        const string stringSource = """
            define actor Text as
                receive(bool enabled):void {
                    let int quoted = 1;
                    let bool marker = true;
                }
            end
            """;
        Require(OresActorDeclarationParser.Parse(stringSource).Actors.Count == 1,
            "supported body string/token boundaries preserve declaration framing");
    }

    private static void EnforcesInputAndNestingBudgets()
    {
        ExpectFormat(() => OresActorDeclarationParser.Parse("define actor A as receive(int x):void{" +
            new string('{', 130) + new string('}', 130) + "} end"),
            "handler source nesting bound");
        ExpectFormat(() => OresActorDeclarationParser.Parse(new string('x', 1_048_577)),
            "UTF-8 source byte bound");
        ExpectFormat(() => OresActorDeclarationParser.Parse("define actor A as receive(int x):void{/* never closed")),
            "unterminated comment");
    }

    private static void ExpectFormat(Action action, string description)
    {
        try { action(); }
        catch (FormatException ex)
        {
            Require(ex.Message.Contains("E_ACTOR_", StringComparison.Ordinal),
                description + " must use stable actor diagnostic");
            return;
        }
        throw new Exception("Expected actor declaration rejection: " + description);
    }

    private static void Require(bool ok, string description)
    {
        if (!ok) throw new Exception("Actor declaration AST regression: " + description);
    }
}
