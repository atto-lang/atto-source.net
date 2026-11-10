using Ores.Net.Compiler;

internal static class ActorDeclarationAstSelfTest
{
    public static void Run()
    {
        ParsesBoundedActorSignaturesAndOpaqueBodies();
        BuildsDeterministicSignatureOnlyMetadata();
        RejectsDuplicateAndUnsupportedDeclarations();
        KeepsCommentsAndStringsLexicallyInert();
        EnforcesInputAndNestingBudgets();
        RejectsReservedActorFailureHooksAndForgedSignatureMetadata();
        Console.WriteLine("PASS actor-declaration-ast-v1|actors=2|handlers=3|negative=17|execution=0");
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

    private static void BuildsDeterministicSignatureOnlyMetadata()
    {
        const string first = """
            define actor Meter as
                receive(self, int amount):void { let int local = amount; }
                reset():void { }
            end
            """;
        const string reorderedAndDifferentBody = """
            define actor Meter as
                reset():void { let bool local = true; }
                receive(int amount):void { /* another opaque body */ }
            end
            """;
        ActorDeclarationSignatureTable a =
            OresActorDeclarationParser.BuildSignatureTable(
                OresActorDeclarationParser.Parse(first));
        ActorDeclarationSignatureTable b =
            OresActorDeclarationParser.BuildSignatureTable(
                OresActorDeclarationParser.Parse(reorderedAndDifferentBody));
        Require(a.Actors.Count == 1 && b.Actors.Count == 1 &&
                a.Actors[0].SignatureFingerprintSha256.Length == 64 &&
                a.Actors[0].SignatureFingerprintSha256 ==
                    b.Actors[0].SignatureFingerprintSha256,
            "signature fingerprint ignores body and declaration-order changes");
        Require(a.Actors[0].Handlers.Count == 2 &&
                a.Actors[0].Handlers[0].ParameterTypes.Count == 1 &&
                a.Actors[0].Handlers[0].ParameterTypes[0] == ActorMessageType.I64,
            "handler signature metadata retains receiver-erased value types");

        var original = OresActorDeclarationParser.Parse(first);
        ActorDeclaration oldActor = original.Actors[0];
        var changedVisibility = new ActorDeclarationUnit(
            new[] { oldActor with { IsPublic = true } }, original.SourceLength);
        var visibilityTable = OresActorDeclarationParser.BuildSignatureTable(changedVisibility);
        Require(visibilityTable.Actors[0].SignatureFingerprintSha256 !=
                a.Actors[0].SignatureFingerprintSha256,
            "visibility is part of deterministic actor declaration signature");

        var invalidSpanHandler = oldActor.Handlers[0] with {
            BodyOffset = original.SourceLength + 1
        };
        var invalidSpan = new ActorDeclarationUnit(
            new[] { oldActor with {
                Handlers = new[] { invalidSpanHandler, oldActor.Handlers[1] }
            } }, original.SourceLength);
        ExpectFormat(() => OresActorDeclarationParser.BuildSignatureTable(invalidSpan),
            "manually constructed actor AST cannot forge an out-of-source body span");
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
                    "literal brace } and text: define actor Fake as end await";
                }
            end
            """;
        ActorDeclarationUnit parsed = OresActorDeclarationParser.Parse(stringSource);
        Require(parsed.Actors.Count == 1 &&
                parsed.Actors[0].Handlers[0].BodySource.Contains(
                    "literal brace } and text: define actor Fake as end await",
                    StringComparison.Ordinal),
            "string contents are opaque and cannot close bodies or declare actors");
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
        ExpectFormat(() => OresActorDeclarationParser.Parse(""),
            "empty actor unit");
    }

    // Actor failure hooks are runtime/supervisor capabilities, not ordinary
    // opaque actor methods. A forged AST must not bypass this admission rule.
    private static void RejectsReservedActorFailureHooksAndForgedSignatureMetadata()
    {
        string[] reservedNames =
        {
            "on_panic", "on_throw", "on_raise", "tear_down",
            "on_recover", "on_throws"
        };
        foreach (string name in reservedNames)
        {
            string expected = name == "on_panic"
                ? "E_ACTOR_PANIC_HOOK_FORBIDDEN"
                : "E_ACTOR_FAILURE_HOOK_UNLOWERED";
            string source = "define actor A as " + name + "():void{} end";
            ExpectExactActorDiagnostic(
                () => OresActorDeclarationParser.Parse(source),
                expected, "declared actor hook " + name);
        }

        const string allowed = """
            define actor A as
                receive(int value):void {
                    "on_panic on_throw on_raise";
                    /* on_panic() { } on_raise() { } */
                }
            end
            """;
        var parsed = OresActorDeclarationParser.Parse(allowed);
        Require(parsed.Actors.Count == 1 &&
                parsed.Actors[0].Handlers[0].Name == "receive",
            "hook lookalikes inside strings/comments must stay inert");

        var original = OresActorDeclarationParser.Parse(
            "define actor A as receive(int value):void{} end");
        foreach (string name in reservedNames)
        {
            var forged = original.Actors[0].Handlers[0] with { Name = name };
            var unit = new ActorDeclarationUnit(
                new[] { original.Actors[0] with {
                    Handlers = new[] { forged }
                } }, original.SourceLength);
            string expected = name == "on_panic"
                ? "E_ACTOR_PANIC_HOOK_FORBIDDEN"
                : "E_ACTOR_FAILURE_HOOK_UNLOWERED";
            ExpectExactActorDiagnostic(
                () => OresActorDeclarationParser.BuildSignatureTable(unit),
                expected, "forged AST actor hook " + name);
        }
        Console.WriteLine("NET_ACTOR_FAILURE_HOOK_ADMISSION_SOURCE_TESTS|cases=13|guest=NOT_EXECUTED");
    }

    private static void ExpectExactActorDiagnostic(
        Action action, string expectedCode, string description)
    {
        try { action(); }
        catch (FormatException ex) when
            (ex.Message.Contains(expectedCode, StringComparison.Ordinal))
        { return; }
        throw new Exception("Actor failure hook admission did not reject: " +
            description + " with " + expectedCode);
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
