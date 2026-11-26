using Ores.Net.Compiler;

internal static class StaticSelectAdmissionTests
{
    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception(why);
    }
    private static void RejectDiagnostic(Action action, string diagnostic, string why)
    {
        try { action(); }
        catch (FormatException e) when (e.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected select-specific rejection: " + why);
    }
    private static void PortableCountedAdmission()
    {
        // Byte-identical source corpus 9a8391ddc4270fd6e1c2a6b1650eff935eb82999.
        // Six positive language targets remain unsupported, NOT guest parity.
        var fixtures = new (string Id, string Sha, string Source, bool Positive)[]
        {
            ("counted-three", "6531efa0689f2a5728b40a6412651e8143e45865", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); writech incoming, 3; writech incoming, 5; writech incoming, 13; writech incoming, 99; val Array[int] values = await select<int>(3) over when readch incoming: int value -> { return value * 2; } end; if values[0] == 6 && values[1] == 10 && values[2] == 26 then return readch incoming - 57; fi return 0; }\n", true),
            ("counted-zero", "42e49fc1eea64b31e86ec722e1457c1ab706a7e1", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); writech incoming, 42; val Array[int] values = await select<int>(0) { when readch incoming: int value -> { return value * 2; } }; return readch incoming; }\n", true),
            ("counted-one", "910062ebf96613d15a16ddb080abd89375d7709c", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); writech incoming, 21; val Array[int] values = await select(1) { when readch incoming: int value -> { return value * 2; } }; return values[0]; }\n", true),
            ("counted-alias", "324837078af3e7eeb9b619d0fb07142f0a0d6291", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); writech incoming, 21; val Future<Array<int>> values = nb select<int>(1) over when readch incoming: int value -> { return value * 2; } end; val Array[int] result = await values; return result[0]; }\n", true),
            ("reject-counted-negative", "e2ff6fdb406b8807b74fa15beaee10a7f9438e22", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); val Array[int] values = await select<int>(-1) { when readch incoming: int value -> { return value * 2; } }; return 0; }\n", false),
            ("reject-counted-bool", "4a52c22442bc8a9fe55eb95b23722595db32c97e", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); val Array[int] values = await select<int>(true) { when readch incoming: int value -> { return value * 2; } }; return 0; }\n", false),
            ("reject-counted-scalar", "a2a2dd184237f17b5e8d455f498719ef1ed01d97", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); val int result = await select<int>(1) { when readch incoming: int value -> { return value * 2; } }; return result; }\n", false),
            ("reject-counted-default", "0c67c1fc8eaf55df6abb2b0fe7d2ce80f169bc69", "suspends fnc main(): int { val Array[int] result = await select<int>(3) { default: -> { return 42; } }; return 0; }\n", false),
            ("reject-counted-write", "63fa3c9add56f3793e6d755f006cd3568acf1f04", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); val Array[int] result = await select<int>(3) { when writech incoming, 42: -> { return 42; } }; return 0; }\n", false),
            ("counted-dynamic", "7c9ea9270d766612de0d502147e0df31b0dc3454", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(4); writech incoming, 3; writech incoming, 5; writech incoming, 13; writech incoming, 99; let int count = 3; val Future<Array<int>> pending = select<int>(count) over when readch incoming: int value -> { return value * 2; } end; count = 1; val Array[int] values = await pending; if values[0] == 6 && values[1] == 10 && values[2] == 26 then return readch incoming - 57; fi return 0; }\n", true),
            ("counted-dynamic-zero", "4707b13d3daebe05ced967418ba120cf801e2d8c", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(1); writech incoming, 42; val int count = 0; val Future<Array<int>> pending = select<int>(count) { when readch incoming: int value -> { return value * 2; } }; val Array[int] values = await pending; if values.size == 0 then return readch incoming; fi return 0; }\n", true),
            ("reject-counted-dynamic-bool", "36cd372a5385e34f2e3bd8dd936e4e0491052633", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(1); val bool count = true; val Array[int] values = await select<int>(count) { when readch incoming: int value -> { return value; } }; return 0; }\n", false),
            ("reject-counted-dynamic-unknown", "0e61da7a98987ed5a182148538b35b8a890abf26", "suspends fnc main(): int { val Channel<int> incoming = Channel.new<int>(1); val Array[int] values = await select<int>(missing) { when readch incoming: int value -> { return value; } }; return 0; }\n", false),
        };
        Require(fixtures.Length == 13 && fixtures.Count(f => f.Positive) == 6, "portable counted fixture inventory");
        foreach (var fixture in fixtures)
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(fixture.Source);
            var gitBlob = System.Text.Encoding.UTF8.GetBytes("blob " + raw.Length + "\0" + fixture.Source);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(gitBlob)).ToLowerInvariant();
            Require(actual == fixture.Sha, "portable counted blob drift: " + fixture.Id);
            RejectDiagnostic(() => OresParser.Parse(fixture.Source),
                "Static select requires checked Channel/Future arbitration", fixture.Id);
            RejectDiagnostic(() => OresStructuredParser.Parse(fixture.Source),
                "Static select requires checked Channel/Future arbitration", fixture.Id);
        }
        foreach (var prefix in new[] {"select", "nb select", "await select", "await nb select"})
        foreach (var header in new[] {"(count)", "<int>(count)", "(count+1)", "<int>(count+1)"})
        foreach (var body in new[] {"{ default: -> { return 42; } }", "over default: -> { return 42; } end"})
        {
            string source = prefix + header + " " + body;
            RejectDiagnostic(() => OresParser.Parse(source),
                "Static select requires checked Channel/Future arbitration", source);
            RejectDiagnostic(() => OresStructuredParser.Parse(source),
                "Static select requires checked Channel/Future arbitration", source);
        }
        Console.WriteLine("NET_COUNTED_PORTABLE_ADMISSION_PASS 13 exact fixtures and 32 isolated forms, both parsers; NOT guest execution");
    }

    public static void Run()
    {
        PortableCountedAdmission();
        // Typed static select must fail with the select-specific diagnostic in
        // both compiler entry points, even when trivia separates the tokens. It is
        // never silently downgraded to scalar arithmetic or a CPU execution region.
        foreach (string source in new[] {
            "select<int> over default: -> { return 42; } end",
            "select /* trivia */ <int> over default: -> { return 42; } end",
            "nb select<int> over default: -> { return 42; } end",
            "try select<int> over default: -> { return 42; } end",
            "do select<int> over default: -> { return 42; } end",
            "do select over default: -> { return 42; } end",
            "nb select over default: -> { return 42; } end",
            "select { default: -> { return 42; } }",
            "select(3) over when readch incoming: int value -> { return value; } end",
            "select /* outer /* inner */ trivia */ (3) { default: -> { return 42; } }",
            "select<int>(3) { default: -> { return 42; } }",
            "await select(3) over default: -> { return 42; } end",
            "await select<int>(3) over default: -> { return 42; } end",
            "nb select(3) { default: -> { return 42; } }",
            "nb /* comment */ select /* count */ (3) { default: -> { return 42; } }",
            "try select(0) { default: -> { return 42; } }",
            "do select(-1) { default: -> { return 42; } }",
            "select(true) { default: -> { return 42; } }",
            "select(1 + 2) { default: -> { return 42; } }"
        })
        {
            RejectDiagnostic(() => OresParser.Parse(source),
                "Static select requires checked Channel/Future arbitration",
                "scalar frontend must fail closed on typed select syntax");
            RejectDiagnostic(() => OresStructuredParser.Parse(source),
                "Static select requires checked Channel/Future arbitration",
                "structured frontend must fail closed on typed select syntax");
        }
        Require(OresStructuredParser.Parse("fnc select(int n):int{return n;} pub fnc main():int{return select((42));}") is not null,
            "ordinary contextual select call must remain accepted in the structured frontend");
        Require(OresParser.Parse("fnc main():int{return 42;} /* select<int> over ignored */") is not null,
            "comment-only typed select must not trigger scalar admission");
        Require(OresStructuredParser.Parse(
            "fnc main()->int { /* select<int> over ignored */ return 42; }").Body.Count == 1,
            "comment-only typed select must not trigger structured admission");
    }
}
