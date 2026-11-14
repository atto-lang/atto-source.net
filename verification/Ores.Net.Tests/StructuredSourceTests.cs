using Ores.Net.Compiler;

internal static class StructuredSourceTests
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("Structured source regression: " + reason);
    }

    private static void Reject(string source, string reason)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException ex)
        {
            Require(ex.Message.Contains("offset", StringComparison.Ordinal) ||
                    ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase),
                reason + ": missing useful diagnostic");
            return;
        }
        throw new Exception("Structured source incorrectly accepted: " + reason);
    }

    private static void RejectFeature(string source, string expectedDiagnostic)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException error)
        {
            Require(error.Message.Contains(expectedDiagnostic, StringComparison.OrdinalIgnoreCase),
                "unsupported feature rejected for wrong reason for source `" + source + "`: " + error.Message);
            Require(error.Message.Contains("offset", StringComparison.Ordinal),
                "unsupported feature missing source position");
            return;
        }
        throw new Exception("Unsupported receiver/capture syntax was admitted");
    }

    private static void RejectScalarStateInjection(string source, string expectedDiagnostic)
    {
        try { _ = OresParser.Parse(source); }
        catch (FormatException error)
        {
            Require(error.Message.Contains(expectedDiagnostic, StringComparison.Ordinal),
                "scalar compiler rejected injected state for wrong reason: " + error.Message);
            Require(error.Message.Contains("offset", StringComparison.Ordinal),
                "scalar injection diagnostic lacks an exact source offset");
            return;
        }
        throw new Exception("Scalar compiler admitted unsupported state injection");
    }

    public static void Run()
    {
        // AST identity and constant provenance must stay independent: a
        // default readonly typed binding is NOT an explicit constexpr const.
        var immutableDefaults = OresStructuredParser.Parse(
            "fnc main():int{int x=7;bool good=true;const int c=7;val int v=7;let int m=7;return x;}");
        Require(immutableDefaults.Body[0] is StructuredLet {
            Name: "x", Kind: StructuredBindingKind.Readonly, ValueType: IrValueType.I64
        }, "int x must be a readonly non-constexpr binding");
        Require(immutableDefaults.Body[1] is StructuredLet {
            Name: "good", Kind: StructuredBindingKind.Readonly, ValueType: IrValueType.Bool
        }, "bool x must be a readonly non-constexpr binding");
        Require(immutableDefaults.Body[2] is StructuredLet {
            Name: "c", Kind: StructuredBindingKind.Const
        }, "explicit const retains its constexpr identity");
        Require(immutableDefaults.Body[3] is StructuredLet {
            Name: "v", Kind: StructuredBindingKind.Val
        }, "val remains an independent single-assignment spelling");
        Require(immutableDefaults.Body[4] is StructuredLet {
            Name: "m", Kind: StructuredBindingKind.Let
        }, "let remains the only rebindable local");
        Reject("fnc main():int{int x=7;x=8;return x;}",
            "readonly type-first rebinding is forbidden");

        // Injected mutable containers are not the current scalar backend's
        // ordinary metadata. Require a feature-specific diagnostic, not a
        // generic invalid-character parse rejection or fake CIL success.
        foreach (var source in new[] {
            "@Inject(MutableStateContainer())\ndefine class Foo as\nstate {}\nend\n",
            "@Inject(MutableStateContainer(scope = #instance))\ndefine class Foo as\nend\n",
            "@StateBacked(MutableStateContainer(scope = #shared))\ndefine class Foo as\nend\n",
            "@MutableStateContainer()\ndefine actor A as\nend\n",
            "@StateContainer()\ndefine singleton S as\nend\n",
            "@Inject(SessionState)\nfnc main():int{return 7;}",
            "@Inject\ndefine class Foo as\nend\n",
            "@Inject<MutableStateContainer()>\ndefine class Foo as\nend\n",
            "@StateContainer()\ndefine class Foo as\nend\n",
            "@Inject(SessionState)\npub fnc main():int{return 42;}\n"
        })
        {
            string annotation = source.StartsWith("@Inject", StringComparison.Ordinal)
                ? "Inject"
                : source.StartsWith("@StateBacked", StringComparison.Ordinal)
                    ? "StateBacked"
                    : source.StartsWith("@MutableStateContainer", StringComparison.Ordinal)
                        ? "MutableStateContainer"
                        : "StateContainer";
            RejectFeature(source, "STATE_INJECTION_UNSUPPORTED: @" + annotation);
            RejectScalarStateInjection(source, "STATE_INJECTION_UNSUPPORTED: @" + annotation);
        }
        var commentOnlyInjection = OresStructuredParser.Parse(
            "// @Inject(MutableStateContainer()) is a comment\nfnc main():int{return 7;}");
        Require(commentOnlyInjection is not null,
            "injection spelling inside a comment is not executable syntax");
        _ = OresParser.Parse(
            "// @Inject(MutableStateContainer()) is a comment\nfnc main():int{return 7;}");
        _ = OresParser.Parse(
            "/* outer @Inject /* inner @StateBacked */ ignored */\nfnc main():int{return 7;}");
        RejectScalarStateInjection(
            "// skipped @Inject\n@StateBacked(MutableStateContainer())\nfnc main():int{return 7;}",
            "STATE_INJECTION_UNSUPPORTED: @StateBacked");

        // Verify annotation offset after ignored comment trivia.
        const string injectionOffsetProbe =
            "/* inert @Inject(Fake()) */\n" +
            "// inert @MutableStateContainer\n\n" +
            " @StateBacked(MutableStateContainer())\n" +
            "fnc main():int{return 7;}";
        int injectionStart = injectionOffsetProbe.IndexOf("@StateBacked", StringComparison.Ordinal);
        Require(injectionStart > 0, "missing executable annotation");
        try {
            _ = OresStructuredParser.Parse(injectionOffsetProbe);
            throw new Exception("Structured parser admitted state injection");
        } catch (FormatException error) {
            Require(error.Message.Contains("STATE_INJECTION_UNSUPPORTED: @StateBacked", StringComparison.Ordinal)
                && error.Message.Contains("offset " + injectionStart, StringComparison.Ordinal),
                "structured parser used comment offset instead of executable annotation");
        }
        try {
            _ = OresParser.Parse(injectionOffsetProbe);
            throw new Exception("Scalar parser admitted state injection");
        } catch (FormatException error) {
            Require(error.Message.Contains("STATE_INJECTION_UNSUPPORTED: @StateBacked", StringComparison.Ordinal)
                && error.Message.Contains("offset " + injectionStart, StringComparison.Ordinal),
                "scalar parser used comment offset instead of executable annotation");
        }

        // Unknown annotations must be explicit errors too, not silently
        // downgraded to scalar expression source.
        foreach (var unknown in new[] {"@NotRegistered", "@InjectExtra"})
        {
            try { _ = OresParser.Parse(unknown + "\nfnc main():int{return 7;}"); }
            catch (FormatException error)
            {
                Require(error.Message.Contains("Annotations require verified compiler lowering", StringComparison.Ordinal),
                    "unknown annotation was misclassified as a state effect");
                continue;
            }
            throw new Exception("Unknown scalar annotation unexpectedly compiled");
        }

        var leadingComment = OresStructuredParser.Parse(
            "// nested annotation @StateBacked(MutableStateContainer(scope = #instance)) is inert\n"
            + "fnc main():int{return 7;}");
        Require(leadingComment is not null,
            "state backing in a comment must not trigger a false-positive capability denial");
        var blockCommentOnly = OresStructuredParser.Parse(
            "/* outer @Inject(SessionState) /* inner @StateBacked(MutableStateContainer(scope = #instance)) */ still comment */\n"
            + "fnc main():int{return 7;}");
        Require(blockCommentOnly is not null,
            "nested block comments must not mint or falsely reject container capabilities");
        // All .NET compilation routes must fail closed on nominal ModelGraphs.
        // The structured parser backs CFG/CIL and linked-module compilation,
        // not only the shipping scalar expression parser.
        foreach (var source in new[] {
            "define model x as Identity() end",
            "define model PriceEstimate() as Identity() end",
            "define pub model x as Identity() end",
            "pub define model x as Identity() end",
            "define /* inert */ model x as Identity() end",
            "fnc main():int{return 42;} define model x as Identity() end",
            "fnc main():int{define model Local as Identity() end return 42;}"
        })
            RejectFeature(source, "MODEL_LOWERING_UNSUPPORTED");
        Require(OresStructuredParser.Parse(
            "fnc main():int{let int model=41;return model+1;}").Body.Count == 2,
            "contextual model remains a valid scalar local identifier");
        Require(OresStructuredParser.Parse(
            "/* define model Fake as Identity() end */ fnc main():int{return 42;}").Body.Count == 1,
            "commented-out model declarations are lexically inert");
        Require(OresStructuredParser.Parse(
            "fnc main():int{return 42;} // define model Fake as Identity() end").Body.Count == 1,
            "line comment model syntax is lexically inert");
        // No executable supervisor/CONTROL source lowering exists in the
        // scalar NativeAOT compiler. A nominal supervisor must not downgrade
        // to an ordinary class, actor, trait, or exported helper.
        foreach (var source in new[] {
            "define supervisor S1 as on_panic(f: Failure): void { return; } end",
            "pub define supervisor S1 as on_panic(f: Failure): void { return; } end",
            "define pub supervisor S1 as on_panic(f: Failure): void { return; } end",
            "define supervisor S1 extends Actor as on_panic(f: Failure): void {} end",
            "define supervisor S1 implements Observer as on_panic(f: Failure): void {} end",
            "define supervisor S1 with RestartPolicy as on_panic(f: Failure): void {} end",
            "define module Workers as define supervisor S1 as on_panic(f: Failure): void {} end end",
            "fnc main():int{define supervisor Local as on_panic(f: Failure): void {} end return 42;}"
        })
            RejectFeature(source, "Source supervisors require typed Failure, owned CONTROL dispatch");
        // Identical text in comments and quoted strings is not executable syntax.
        var supervisorComment = OresStructuredParser.Parse(
            "fnc main():int{return 42;} // define supervisor S1 extends Actor");
        Require(supervisorComment.Body.Count == 1,
            "comments must not create supervisor admission failures");
        // Linked import discovery runs before full source parsing and must not
        // inspect dependency paths for malformed input hidden in comments.
        // The production entry point is internal; reflection is test-only.
        var readLinkedImports = typeof(OresStructuredParser).GetMethod(
            "ReadLinkedImports",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("Linked import admission entry point missing");
        foreach (var (source, expectedError) in new (string, string)[] {
            ("/* hidden " + '\0' + " */", "Source contains NUL"),
            ("/* hidden " + (char)0xD800 + " */", "Invalid UTF-16"),
            ("// hidden " + (char)0xDC00 + "\n", "Invalid UTF-16"),
            ("/* hidden " + (char)0xDC00 + " */", "Invalid UTF-16")
        })
        {
            try
            {
                _ = readLinkedImports.Invoke(null, new object?[] { source });
            }
            catch (System.Reflection.TargetInvocationException failure)
                when (failure.InnerException is FormatException error &&
                      error.Message.Contains(expectedError, StringComparison.Ordinal) &&
                      error.Message.Contains("offset", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception(
                "Linked import discovery admitted malformed source before full parse: " +
                expectedError);
        }
        _ = readLinkedImports.Invoke(null, new object?[] { "/* valid \uD83D\uDD12 */" });

        // Local value types are block-local declarations on the structured
        // frontend. They are admitted into the AST but deliberately rejected
        // by CFG/CIL lowering until value layout and construction are verified.
        var localTypes = OresStructuredParser.Parse("""
            fnc main():int {
                struct Pair { int left; int right }
                record Status { bool ready, int code };
                return 42;
            }
            """);
        Require(localTypes.Body.Count == 3,
            "local struct/record declarations are preserved as statements");
        Require(localTypes.Body[0] is StructuredLocalTypeDecl {
            Name: "Pair", Kind: StructuredLocalTypeKind.Struct, Fields.Count: 2
        } pair && pair.Fields[0] == new StructuredLocalField("left", IrValueType.I64)
               && pair.Fields[1] == new StructuredLocalField("right", IrValueType.I64),
            "local struct field order and scalar types are preserved");
        Require(localTypes.Body[1] is StructuredLocalTypeDecl {
            Name: "Status", Kind: StructuredLocalTypeKind.Record, Fields.Count: 2
        } status && status.Fields[0] == new StructuredLocalField("ready", IrValueType.Bool)
                 && status.Fields[1] == new StructuredLocalField("code", IrValueType.I64),
            "local record field order and scalar types are preserved");
        Reject("fnc main():int{struct Pair{int x;int x}return 1;}",
            "duplicate local value-type fields are forbidden");
        Reject("fnc main():int{struct Pair{int x}if true{record Pair{int y}}return 1;}",
            "nested local value types cannot shadow an active local type");
        foreach (var nominal in new[] {
            "fnc main():int{class Local{}return 1;}",
            "fnc main():int{actor Local{}return 1;}",
            "fnc main():int{define class Local{}return 1;}",
            "fnc main():int{define actor Local{}return 1;}",
            "fnc main():int{let int x=1;define class Local{}return x;}",
            "fnc main():int{if true {define actor Local{}}return 1;}"
        })
            RejectFeature(nominal, "Local class/actor declarations are not permitted");
        var tooManyFields = "fnc main():int{struct Big{" +
            string.Join(";", Enumerable.Range(0, 65).Select(i => "int f" + i)) +
            "}return 1;}";
        Reject(tooManyFields, "local value-type field budget is bounded");
        try
        {
            _ = OresStructuredCfgLowering.Lower(
                "fnc main():int{struct Pair{int left;int right}return 42;}");
            throw new Exception("Local value type reached CFG lowering without layout support");
        }
        catch (FormatException ex)
        {
            Require(ex.Message.Contains("requires verified value layout and CIL lowering",
                    StringComparison.Ordinal) &&
                    ex.Message.Contains("offset", StringComparison.Ordinal),
                "local value types must fail at the explicit CFG/CIL capability gate");
        }

        // Java reference binding kinds: compile-time const, single-write val,
        // and assignable let. This tests typed parser admission.
        var bindingKinds = OresStructuredParser.Parse("""
            fnc main():int {
                const int answer = 6 * 7;
                const int next = answer + 1;
                val int retained = next - 1;
                let int mutable = retained;
                mutable = mutable + 0;
                return mutable;
            }
            """);
        Require(bindingKinds.Body[0] is StructuredLet {
            Name: "answer", Kind: StructuredBindingKind.Const,
            Initializer: StructuredInt { Value: 42 }
        }, "const evaluation replaces the initializer with a checked typed literal");
        Require(bindingKinds.Body[1] is StructuredLet {
            Name: "next", Kind: StructuredBindingKind.Const,
            Initializer: StructuredInt { Value: 43 }
        }, "const references propagate their checked compile-time value");
        Require(bindingKinds.Body[2] is StructuredLet {
            Name: "retained", Kind: StructuredBindingKind.Val
        }, "val preserves runtime single-assignment marker");
        Require(bindingKinds.Body[3] is StructuredLet {
            Name: "mutable", Kind: StructuredBindingKind.Let,
            Initializer: StructuredRead { Name: "retained" }
        }, "val remains a runtime read and let remains mutable");
        Require(bindingKinds.Body[4] is StructuredSet { Name: "mutable" },
            "let assignment remains supported");

        var constantBool = OresStructuredParser.Parse("""
            fnc main():int {
                const bool enabled = 6 * 7 eq 42;
                if enabled { return 42; } else { return 0; }
            }
            """);
        Require(constantBool.Body[1] is StructuredIf {
            Condition: StructuredBool { Value: true }
        }, "compile-time bool may appear in typed conditionals");
        // Java/LLVM/.NET scalar inference and checked-cast conformance.
        foreach (var declaration in new[] {
            "int x=7;const y=x;const int z=y as int;return z+35;",
            "bool flag=true;if flag{return 42;}else{return 0;}",
            "int x=(2+5) as int;return x+35;",
            "const int x=7;const y=7 as int;return x+y+28;",
            "const x=7;const int y=x as int;return y+35;",
            "const x:int=7;return x+35;",
            "const x=7 as int;return x+35;",
            "return (7 as int)+35;",
            "return (7 as int as int)+35;",
            "const x=(2+5) as int;return x+35;",
            "const x=7 as int as int;return x+35;",
            "const x=7;const y=x as int;return y+35;",
            "const x=true; if x {return 42;}else{return 0;}",
            "const bool x=true as bool;if x{return 42;}else{return 0;}",
            "const x=(7 eq 7) as bool;if x{return 42;}else{return 0;}",
            "let x=7 as int;x=42;return x;",
            "val x=42;const y=x as int;return y;",
            "val bool x=true;const y=x as bool;if y{return 42;}else{return 0;}",
            "const x=7;if true{const nested=x as int;return nested+35;}else{return 0;}",
            "const x=7;return (x+35) as int;"
        })
        {
            var unit = OresStructuredParser.Parse("fnc main():int{" + declaration + "}");
            Require(unit.Body.Count > 0, "valid inferred/cast source rejected: " + declaration);
        }
        var checkedBindings = OresStructuredParser.Parse(
            "fnc main():int{const int x=7;const y=7 as int;const z=7;return x+y+z+21;}");
        foreach (var statement in checkedBindings.Body.Take(3))
            Require(statement is StructuredLet {
                Kind: StructuredBindingKind.Const, ValueType: IrValueType.I64
            }, "all three scalar declarations infer identical immutable int type");
        var inferredBool = OresStructuredParser.Parse(
            "fnc main():int{const ok=true as bool;if ok{return 42;}else{return 0;}}");
        Require(inferredBool.Body[0] is StructuredLet {
            Kind: StructuredBindingKind.Const, ValueType: IrValueType.Bool
        }, "bool cast must preserve bool inference, never i64 storage type");
        foreach (var invalid in new[] {
            "int x=true;return 42;",
            "bool x=7;return 42;",
            "int x=7;x=8;return x;",
            "const x=7 as bool;return 42;",
            "return 7 as bool;",
            "return true as int;",
            "return 9223372036854775808 as int;",
            "const x=true as int;return 42;",
            "const x=7 as? int;return 42;",
            "const x=7 as Thing;return 42;",
            "const int x=true;return 42;",
            "const bool x=7;return 42;",
            "const x=true;const int y=x;return 42;",
            "const x=7;const bool y=x;return 42;",
            "const x=7 as bool as int;return 42;",
            "const x=7 as int;x=8;return 42;",
            "const bool x=7 as int;return 42;",
            "const x=true;return x+1;",
            "let x=true;x=7;return 42;",
            "const x=missing;return 42;",
            "const x=x;return 42;"
        }) Reject("fnc main():int{" + invalid + "}", "scalar cast/inference fail-closed " + invalid);

        var runtimeBool = OresStructuredParser.Parse("""
            fnc main():int {
                val bool enabled = true;
                if enabled { return 42; } else { return 0; }
            }
            """);
        Require(runtimeBool.Body[1] is StructuredIf {
            Condition: StructuredRead { Name: "enabled" }
        }, "runtime val retains a typed read");

        Reject("fnc main():int{val int v=1;v=2;return v;}",
            "val is single-assignment");
        Reject("fnc main():int{const int v=1;v=2;return v;}",
            "const is immutable");
        Reject("fnc main():int{val bool v=true;if v {v=false;}return 1;}",
            "nested blocks cannot mutate outer val");
        // Unlike a compile-time constant-expression requirement, Oreslang
        // const is an immutable *binding* and may be initialized at runtime.
        foreach (var source in new[] {
            "fnc main():int{let int x=2;const int c=x;return c;}",
            "fnc main():int{val int x=2;const int c=x;return c;}"
        })
        {
            var runtimeConst = OresStructuredParser.Parse(source);
            Require(runtimeConst.Body[1] is StructuredLet {
                Name: "c", Kind: StructuredBindingKind.Const,
                Initializer: StructuredRead { Type: IrValueType.I64 }
            }, "runtime-initialized const must not acquire fake compile-time value");
        }
        Reject("fnc main():int{const int x=9223372036854775807+1;return x;}",
            "const arithmetic must trap during admission");
        Reject("fnc main():int{const int x=-(-9223372036854775808);return x;}",
            "const negation overflow is rejected during admission");
        Reject("fnc main():int{const int x=3;const int x=4;return x;}",
            "duplicate const binding is forbidden");
        Reject("fnc main():int{val int x=x;return x;}",
            "self-reference cannot bypass single-assignment");
        Reject("fnc helper(int x):int{x=2;return x;}fnc main():int{return helper(1);}",
            "typed helper parameters are immutable runtime bindings");


        foreach (var form in new[] {
            "select { default: { return 1; } }",
            "select over default: { return 1; } end",
            "do select { default: { return 1; } }",
            "do select over default: { return 1; } end",
            "nb select { default: { return 1; } }",
            "nb /* prefix */ select /* delimiter */ over default: { return 1; } end",
            "do nb select { default: { return 1; } }",
            "do nb select over default: { return 1; } end",
            "try select { default: { return 1; } }",
            "try select over default: { return 1; } end"
        })
        {
            const string diagnostic =
                "Static select requires checked Channel/Future arbitration, actor-lane continuation, ownership and cancellation lowering in the .NET backend";
            var source = "fnc main():int{" + form + " return 0;}";
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains(diagnostic, StringComparison.Ordinal) &&
                        ex.Message.Contains("offset", StringComparison.Ordinal),
                    "static select must fail at the explicit backend capability gate: " + ex.Message);
                continue;
            }
            throw new Exception(".NET structured frontend silently admitted unsupported select: " + form);
        }
        Require(OresStructuredParser.Parse(
            "fnc main():int{/* select over default: {} end */return 1;}").Body.Count == 1,
            "select text in a comment must not trigger the capability gate");
        Require(OresStructuredParser.Parse(
            "fnc main():int{let int select=1;return select;}").Body.Count == 2,
            "select identifier must not trigger the static-select capability gate");

        foreach (var form in new[] {
            "select { default: { return 1; } }",
            "select over default: { return 1; } end",
            "do select { default: { return 1; } }",
            "do select over default: { return 1; } end",
            "nb select { default: { return 1; } }",
            "nb /* keep trivia */ select over default: { return 1; } end",
            "do nb select { default: { return 1; } }",
            "do nb select over default: { return 1; } end",
            "try select { default: { return 1; } }",
            "try select over default: { return 1; } end"
        })
        {
            try { _ = OresParser.Parse("fnc main():int{" + form + " return 0;}"); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains(
                        "Static select requires checked Channel/Future arbitration",
                        StringComparison.Ordinal) &&
                        ex.Message.Contains("offset", StringComparison.Ordinal),
                    "shipping parser select rejection was incidental: " + ex.Message);
                continue;
            }
            throw new Exception(".NET shipping scalar parser silently admitted unsupported select: " + form);
        }
        Require(OresParser.Parse(
            "fnc main():int{/* nb select over default: {} end */return 1;}") is Literal,
            "shipping parser must ignore select text inside comments");

        foreach (var parse in new Func<string, object>[] {
            source => OresParser.Parse(source),
            source => OresStructuredParser.Parse(source)
        })
        {
            try { _ = parse("fnc main():int{nb select();return 0;}"); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("execution region 'nb'", StringComparison.Ordinal),
                    "call-like nb select spelling must preserve the existing nb capability gate: " +
                    ex.Message);
                continue;
            }
            throw new Exception("call-like nb select spelling unexpectedly bypassed nb admission");
        }

        foreach (var source in new[] {
            "define inverted class Particle as x: int; end",
            "define pub inverted class Particle as x: int; end",
            "pub define inverted class Particle as x: int; end"
        })
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains(
                        "Inverted class requires checked bracket-row and ownership lowering",
                        StringComparison.Ordinal) &&
                        ex.Message.Contains("offset", StringComparison.Ordinal),
                    "inverted class must fail through the dedicated backend gate: " + ex.Message);
                continue;
            }
            throw new Exception(".NET frontend admitted unlowered inverted class source");
        }
        foreach (var source in new[] {
            "define inverted struct Position as float x; float y; end",
            "define pub inverted struct Position as float x; end",
            "pub define inverted struct Position as float x; end",
            "define /* declaration comment */ inverted /* layout comment */ struct Position as float x; end"
        })
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains(
                        "Inverted struct requires checked value-row, one-level packed columns and ownership lowering",
                        StringComparison.Ordinal) &&
                        ex.Message.Contains("offset", StringComparison.Ordinal),
                    "inverted struct must fail through the dedicated backend gate: " + ex.Message);
                continue;
            }
            throw new Exception(".NET frontend admitted unlowered inverted struct source");
        }
        // The lookahead must not label an absent or unrelated declaration
        // kind as an inverted CLASS. These are malformed source admissions,
        // not claims that any nominal or inverted lowering is supported.
        foreach (var source in new[] {
            "define inverted",
            "define inverted actor Position as end",
            "define pub inverted trait Position as end",
            "pub define /* declaration comment */ inverted /* kind comment */ bogus Position as end"
        })
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains(
                        "Inverted declaration requires class or struct kind",
                        StringComparison.Ordinal) &&
                        ex.Message.Contains("offset", StringComparison.Ordinal),
                    "malformed inverted kind must fail through the dedicated gate: " + ex.Message);
                continue;
            }
            throw new Exception(".NET frontend admitted malformed inverted declaration: " + source);
        }
        // Lexical evidence only: inert comments cannot accidentally become a
        // source layout capability or trigger the inverted declaration gate.
        Require(OresStructuredParser.Parse(
            "fnc main():int{return 42;} // define inverted struct Position") is not null,
            "inverted declaration text inside a comment must remain inert");

        // Shared implicit-val/typed-initialize feature gate. This .NET
        // bootstrap does not have nominal class layouts or CIL constructors.
        // Even sources accepted by Java/LLVM must reject here with an
        // explicit unsupported-capability diagnostic, never fake execution.
        foreach (var source in new[] {
            "define class C as int id: 42; end",
            "define class C as val int id: 42; end",
            "define class C as let int id: 1; end",
            "define class C as final int id: 1; end",
            "define class C as const int id: 1; end",
            "define class C as final int id; initialize(int n) -> void :: self.id=n; end end",
            "define class C as\n final int id;\n initialize(int n) -> void\n self.id=n;\n end\nend",
            "define class C as\n final int id;\n initialize(int n): void\n self.id=n;\n end\nend",
            "define class C as final int id; initialize(int n): void { self.id=n; } end",
            "define class C as initialize() -> void { return; } end",
            "define class C as initialize(int n) as self.id=n; end end",
            "define class C as initialize(int n) -> int { return n; } end",
            "define class C as final int id; initialize(int n) -> void :: self.id=n; self.id=n; end end",
            "define class C as let int id; initialize() -> void {} end",
            "define class C as let int id; initialize(int n) -> void :: publish(self); self.id=n; end end",
            "define class Parent as val int id; end define class Child extends Parent as initialize() -> void {} end",
            "pub define class C as pub val int id: 42; end",
        })
        {
            RejectFeature(source,
                "Nominal class/actor methods require receiver-erased method slots");
        }
        // Keywords in comments must not become a global word-scanning gate.
        Require(OresStructuredParser.Parse(
            "fnc main():int{/* initialize -> void :: class val */return 42;}")
                .Body.Count == 1,
            "class/initialize words in comments must preserve scalar execution");

        // Canonical Java foo() == foo(self) uses one caller-visible slot.
        // The .NET bootstrap has neither nominal class AST nor CIL dispatch:
        // validate a *specific* fail-closed diagnostic for the exact sources
        // instead of calling parser acceptance "implementation parity".
        foreach (var source in new[] {
            "define class X1 as foo():void {} end",
            "define class X2 as foo(self):void {} end",
            "define class X as foo():void {} foo(self):void {} end",
            "define class X as foo(int x):void {} foo(self,int x):void {} end",
            "define class X as foo(self,int x):int {return x;} end",
            "define class X as foo(mut self):void {} end",
            "define actor X as on_start(self):void {} end"
        })
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("receiver-erased method slots", StringComparison.Ordinal)
                        && ex.Message.Contains("Native AOT CIL lowering", StringComparison.Ordinal)
                        && ex.Message.Contains("offset", StringComparison.Ordinal),
                    "class/actor must be rejected by a specific parity gate: " + ex.Message);
                continue;
            }
            throw new Exception(".NET structured frontend silently admitted unlowered method source: " + source);
        }
        foreach (var source in new[] {
            "define enum Color as\n  Red\nend\nfnc main():int{return 1;}",
            "fnc main():int{return 1;}\ndefine enum Color as\n  Red\nend"
        })
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex) when (
                ex.Message.Contains("nominal enum overloads require compiler-bound dispatch",
                    StringComparison.Ordinal)) { continue; }
            throw new Exception("Structured .NET source accepted unsupported nominal enum syntax");
        }
        Require(OresStructuredParser.Parse(
            "fnc main():int{return 1;} // enum stays in comment").Body.Count == 1,
            "enum word in a comment is not a declaration");
        Require(OresStructuredParser.Parse(
            "fnc main():int{/* enum Color /* enum Mode */ nested */return 1;}").Body.Count == 1,
            "nested block-comment enum tokens are non-executable");
        Require(OresStructuredParser.Parse(
            "fnc main():int{return 1;} /* enum Color */").Body.Count == 1,
            "trailing block-comment enum tokens are non-executable");
        static void BlockCommentReject(string source, string reason)
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex) when (
                ex.Message.Contains(reason, StringComparison.Ordinal) &&
                ex.Message.Contains("offset", StringComparison.Ordinal)) { return; }
            throw new Exception("Structured block-comment diagnostic failed: " + reason);
        }
        BlockCommentReject("fnc main():int{return 1;} /* enum Color",
            "Unterminated block comment");
        // The pre-lexing Unicode validator must also inspect ignored text.
        BlockCommentReject("fnc main():int{return 1;} /* " + ((char)0xD800) + " */",
            "Invalid UTF-16");
        BlockCommentReject("fnc main():int{return 1;} /* " + ((char)0) + " */",
            "Source contains NUL");
        // Structural admission runs before lexing and owns the public
        // 64-level limit. Preserve its canonical failure instead of expecting
        // the private lexer's larger defensive bound to be reached.
        const string commentPrefix = "fnc main():int{return 1;} ";
        Require(OresStructuredParser.Parse(commentPrefix +
            string.Concat(Enumerable.Repeat("/*", 64)) +
            string.Concat(Enumerable.Repeat("*/", 64))).Body.Count == 1,
            "64 nested comments must remain valid at the admission boundary");
        try
        {
            _ = OresStructuredParser.Parse(commentPrefix +
                string.Concat(Enumerable.Repeat("/*", 65)) +
                string.Concat(Enumerable.Repeat("*/", 65)));
            throw new Exception("65 nested comments bypassed structural admission");
        }
        catch (FormatException ex) when (ex.Message ==
            $"Structural admission comment nesting exceeded at offset {commentPrefix.Length + 128}") { }


        const string sum = """
            define pub fnc main(): int {
                let int total = 0;
                let int i = 0;
                while i < 10 {
                    total = total + i;
                    i = i + 1;
                }
                return total;
            }
            """;
        var parsed = OresStructuredParser.Parse(sum);
        Require(parsed.Name == "main" && parsed.Body.Count == 4,
            "structured main statement count");
        Require(parsed.Body[0] is StructuredLet { Name: "total",
            ValueType: IrValueType.I64, Initializer: StructuredInt { Value: 0 } },
            "typed initialized integer");
        Require(parsed.Body[2] is StructuredWhile {
            Condition: StructuredBinary {
                Operator: StructuredOperator.LessThanI64,
                Type: IrValueType.Bool
            }, Body.Count: 2
        }, "typed signed comparison and loop body");
        Require(parsed.Body[3] is StructuredReturn {
            Value: StructuredRead { Name: "total", Type: IrValueType.I64 }
        }, "typed return");
        Require(!parsed.Body.IsReadOnlyButMutable(), "AST must be immutable snapshot");

        var diamond = OresStructuredParser.Parse("""
            fnc main(): int {
                let int x = 0;
                let bool condition = 2 == 2;
                if condition {
                    x = 11;
                } else {
                    x = 22;
                }
                return x;
            }
            """);
        Require(diamond.Body[2] is StructuredIf {
            Condition: StructuredRead { Type: IrValueType.Bool },
            Then.Count: 1, Else.Count: 1
        }, "if/else typed joining assignments");

        var earlyReturn = OresStructuredParser.Parse("""
            pub define fnc main(): int {
                if true { return -9223372036854775808; }
                else { return 7; }
            }
            """);
        Require(earlyReturn.Body.Count == 1 &&
            earlyReturn.Body[0] is StructuredIf {
                Then.Count: 1, Else.Count: 1
            }, "both-branch early return");
        Require(OresStructuredParser.Parse("fnc main():int{return 2+3*4;}").Body[0]
            is StructuredReturn { Value: StructuredBinary {
                Operator: StructuredOperator.Add
            } }, "arithmetic AST operator precedence");

        var arithmetic76 = OresStructuredParser.Parse("""
            pub fnc main(): int {
              return 100 - (3 + 2) - 19;
            }
            """);
        Require(arithmetic76.Body[0] is StructuredReturn {
            Value: StructuredBinary {
                Operator: StructuredOperator.Subtract,
                Left: StructuredBinary {
                    Operator: StructuredOperator.Subtract,
                    Left: StructuredInt { Value: 100 },
                    Right: StructuredBinary {
                        Operator: StructuredOperator.Add,
                        Left: StructuredInt { Value: 3 },
                        Right: StructuredInt { Value: 2 }
                    }
                },
                Right: StructuredInt { Value: 19 }
            }
        }, "shared arithmetic-76 typed AST");

        static void RejectTupleV2(string source, string reason)
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("Tuple-v2", StringComparison.Ordinal) &&
                        ex.Message.Contains("offset", StringComparison.Ordinal),
                    reason + ": tuple-v2 rejection was incidental: " + ex.Message);
                return;
            }
            throw new Exception("Tuple-v2 source incorrectly accepted: " + reason);
        }
        RejectTupleV2("fnc main(tuple (int,int) value):int{return 1;}",
            "explicit tuple parameter");
        RejectTupleV2("fnc main(structural (int,int) value):int{return 1;}",
            "structural tuple parameter");
        RejectTupleV2("fnc main((int,int) value):int{return 1;}",
            "shorthand tuple parameter");
        RejectTupleV2("fnc main():tuple (int,int){return 1;}",
            "tuple return");
        RejectTupleV2("fnc main():int{return call(...pair);}",
            "explicit tuple spread");

        foreach (var operation in new[] { "ref", "unref", "deref", "deref mut", "raw" })
        {
            try { _ = OresStructuredParser.Parse(
                $"fnc main():int{{return rt {operation} value;}}"); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("is not valid Oreslang source",
                    StringComparison.Ordinal),
                    "structured pointer rejection must identify invalid source syntax");
                continue;
            }
            throw new Exception("Structured source incorrectly accepted pointer operation: rt " +
                operation);
        }

        foreach (var operation in new[] { "copy", "share", "take", "borrow", "mut borrow" })
        {
            try { _ = OresStructuredParser.Parse(
                $"fnc main():int{{return rt {operation} value;}}"); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("requires typed actor-arena lowering",
                    StringComparison.Ordinal),
                    "structured ownership rejection must name actor-arena lowering");
                continue;
            }
            throw new Exception("Structured source incorrectly accepted ownership operation: rt " +
                operation);
        }
        try
        {
            _ = OresStructuredParser.Parse(
                "fnc main():int{return rt borrow mut value;}");
            throw new Exception("Structured source admitted legacy rt borrow mut");
        }
        catch (FormatException ex)
        {
            Require(ex.Message.Contains("use 'rt mut borrow'", StringComparison.Ordinal),
                "legacy mutable borrow diagnostic must name canonical spelling");
        }

        foreach (var source in new[] {
            "fnc main():int{return &value;}",
            "fnc main():int{return &mut value;}",
            "fnc main():int{return *value;}",
            "fnc main():int{return rt borrow mut value;}",
            "fnc main():Pointer<int>{return value;}"
        })
            Reject(source, "pointer/reference spellings and legacy mutable-borrow syntax");

        // AST height must be bounded even when the source is a flat chain.
        // Grammar recursion depth alone cannot enforce this invariant.
        var maxHeightExpression = string.Join("+", Enumerable.Repeat("1", 128));
        Require(OresStructuredParser.Parse("fnc main():int{return " +
            maxHeightExpression + ";}").Body[0] is StructuredReturn,
            "128-leaf left-associative sum stays inside AST height budget");
        Reject("fnc main():int{return " +
            string.Join("+", Enumerable.Repeat("1", 129)) + ";}",
            "129-leaf sum exceeds AST height limit");
        Reject("fnc main():int{return " +
            string.Join("*", Enumerable.Repeat("1", 129)) + ";}",
            "129-leaf product exceeds AST height limit");
        Reject("fnc main():int{return -(" +
            string.Join("+", Enumerable.Repeat("1", 128)) + ");}",
            "negation cannot silently exceed AST height limit");

        static void ExpectAt(string source, string message, string identifier)
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                var expected = source.IndexOf(identifier, StringComparison.Ordinal);
                Require(ex.Message.Contains($"offset {expected}", StringComparison.Ordinal),
                    message + ": expected offset " + expected + ", got " + ex.Message);
                return;
            }
            throw new Exception("Structured source incorrectly accepted: " + message);
        }
        ExpectAt("fnc main():int{return missing;}",
            "undefined read points to identifier", "missing");
        ExpectAt("fnc main():int{missing=2;return 1;}",
            "undefined assignment points to identifier", "missing");
        ExpectAt("fnc main():int{let int x=1;let int x=2;return x;}",
            "duplicate declaration points to second identifier", "x=2");

        // The AST stores names, not unique binding IDs; enforce the
        // no-shadowing contract until SSA lowering has explicit symbol IDs.
        ExpectAt("fnc main():int{let int x=1;if true {let int x=2;}return x;}",
            "nested shadowing", "x=2");
        ExpectAt("fnc main():int{let bool ok=true;while ok {let int ok=1;}return 0;}",
            "cross-type shadowing", "ok=1");
        Require(OresStructuredParser.Parse("fnc main():int{let int x=1;if true {x=2;}return x;}")
            .Body.Count == 3, "assignment to enclosing binding remains legal");
        Require(OresStructuredParser.Parse(
            "fnc main():int{if true {let int x=1;}else{let int x=2;}return 0;}")
            .Body.Count == 2, "same spelling in sibling lexical scopes is legal");

        // Seeded adversarial mutations of a valid typed source program.
        // All outcomes must be either a typed AST or a FormatException;
        // indexing failures, recursion faults and other exceptions fail CI.
        const string baseProgram = "fnc main():int{let int i=0;while i<4 {i=i+1;}return i;}";
        const string replacements = "abcXYZ01(){};:+-*<=>@/ ";
        var fuzzRandom = new Random(17303);
        int rejectedMutations = 0;
        for (int mutation = 0; mutation < 512; mutation++)
        {
            var changed = baseProgram.ToCharArray();
            int position = fuzzRandom.Next(changed.Length);
            changed[position] = replacements[fuzzRandom.Next(replacements.Length)];
            string candidate = new(changed);
            try { _ = OresStructuredParser.Parse(candidate); }
            catch (FormatException) { rejectedMutations++; }
        }
        Require(rejectedMutations >= 128,
            "adversarial mutation corpus must exercise substantial parser rejection");
        // Until typed aggregate/callable lowering lands, structural source must
        // fail closed rather than silently compiling as scalar-only CIL.
        Reject("structural fnc main():int{return 1;}",
            "structural declaration unsupported in bootstrap");
        Reject("fnc main(structural Shape value):int{return 1;}",
            "structural parameters unsupported in bootstrap");
        Reject("fnc main():int{match 1 over on _ -> {return 1;} end}",
            "match-over unsupported in bootstrap");
        Reject("fnc take(tuple (int, int, string?) v):int{return 1;}",
            "omittable positional tuple is not lowered by CIL bootstrap");
        Reject("fnc take((int, int, string?) v):int{return 1;}",
            "tuple shorthand is not lowered by CIL bootstrap");
        Reject("fnc take(structural (int, int, string?) v):int{return 1;}",
            "structural positional width view is not lowered by CIL bootstrap");

        // The shipping arithmetic frontend is intentionally unchanged.
        // Stage-one binding names must remain unambiguous.
        Require(OresParser.Parse("fnc main():int{return 2+3;}") is Binary,
            "legacy verified arithmetic parser still works");
        Reject("fnc main():int{let int x=x; return x;}", "self-reference");
        Reject("fnc main():int{return missing;}", "undefined read");
        Reject("fnc main():int{missing=2;return 1;}", "undefined assignment");
        Reject("fnc main():int{let int x=1;let int x=2;return x;}",
            "same-scope duplicate binding");
        Reject("fnc main():int{let int x=true;return 0;}",
            "type mismatch on declaration");
        Reject("fnc main():int{let bool ok=false;ok=3;return 0;}",
            "type mismatch on assignment");
        Reject("fnc main():int{if 1 {return 0;}else{return 1;}}",
            "non-boolean if condition");
        Reject("fnc main():int{while 1 {return 0;}return 1;}",
            "non-boolean while condition");
        Reject("fnc main():int{if true {return 0;}}",
            "missing else return path");
        Reject("fnc main():int{while true {return 0;}}",
            "loop is not proof of return");
        Reject("fnc main():int{return true;}", "wrong return type");
        Reject("fnc main():int{return 1;let int x=2;}",
            "unreachable declaration");
        Reject("fnc main():int{if true {return 1;}else{return 2;} return 3;}",
            "unreachable after both branches return");
        Reject("fnc main():int{if true {let int x=1;}return x;}",
            "block local escaped scope");
        Reject("fnc main():int{let bool x=true==false;return 0;}",
            "bool equality unsupported in first CFG slice");
        Reject("fnc main():int{let bool b=true;return b+1;}",
            "boolean arithmetic");
        Reject("fnc main():int{let int x=1/2;return x;}",
            "int division is not i64 in canonical semantics");
        Reject("fnc main():int{let int x=9223372036854775808;return x;}",
            "literal exceeding i64 range");
        Reject("fnc main():int{let int x=-9223372036854775809;return x;}",
            "negative literal below i64 range");
        Reject("fnc main():int{let int x=1@2;return x;}",
            "invalid character");
        foreach (string real in new[] { "0.125", "1e10", "1E-10", "123.456d", "123d",
                                       "1_000.5", "1_0e+2", "1_2d" })
        {
            try { _ = OresStructuredParser.Parse("fnc main():int{return " + real + ";}"); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains(
                    "Floating-point/decimal literal requires typed numeric lowering",
                    StringComparison.Ordinal),
                    "real/decimal literal must fail before token splitting: " + real);
                continue;
            }
            throw new Exception("Structured source incorrectly accepted real/decimal literal: " + real);
        }
        Reject("fnc main():int{let int x=1;}\u0000", "NUL");
        Reject("fnc main():int{return 1;}\uD800", "invalid UTF16");
        Reject("fnc main():int{return 1;}//comment\nextra", "trailing syntax");
        Reject("fnc main():int{return 1;".PadRight(140, ' '),
            "unterminated block");
        Reject(new string(' ', 1_048_577), "source length bound");
        Reject("fnc main():int{return " +
            new string('(', 150) + "1" + new string(')', 150) +
            ";}", "nested expression depth bound");

        // The parser must not publish a block-scoped local after its block.
        var nested = OresStructuredParser.Parse("""
            fnc main(): int {
                let int a = 9;
                if a < 10 {
                    let int b = a + 1;
                    a = b;
                } else {
                    let int b = a - 1;
                    a = b;
                }
                return a;
            }
            """);
        Require(nested.Body[1] is StructuredIf {
            Then.Count: 2, Else.Count: 2
        }, "parallel branch-local scopes");

        // Abstract class admission must reject for the missing virtual-dispatch
        // capability, not merely because the generic scalar parser sees a
        // surprising token. The generic contract is intentionally unsupported.
        RejectFeature("define abstract class Framework as "
            + "private abstract receive(int x): int; end",
            "Private abstract class hooks require typed virtual dispatch");

        // Contextual capture keywords remain valid ordinary scalar local names.
        foreach (string captureName in new[] { "lex", "nlex" })
        {
            var plainCaptureName = OresStructuredParser.Parse(
                $"fnc main():int{{let int {captureName}=42;return {captureName};}}");
            Require(plainCaptureName.Body.Count == 2 &&
                    plainCaptureName.Body[1] is StructuredReturn {
                        Value: StructuredRead { Type: IrValueType.I64 } read
                    } && read.Name == captureName,
                captureName + " remains an ordinary scalar identifier");
        }
        // Ignored comment text cannot activate unsupported capture guards.
        Require(OresStructuredParser.Parse(
            "fnc main():int{// lex[self] || -> nlex[self] critical |state| -> 1\nreturn 42;}"
            ).Body.Count == 1, "capture syntax in a comment remains inert");

        // Until this backend has a class-aware method table, capture and
        // borrow analysis, it must reject these canonical constructs rather
        // than interpreting them as scalar code or ignoring a receiver marker.
        foreach (string unsupported in new[]
        {
            "define class Foo as fnc read(self, int x): int { return x; } end",
            "define actor Foo as receive(self, int x): void { return; } end",
            "define actor Foo as on_start(self): void { return; } end",
            "define actor Foo as receive(ActorMail<int> mail): void { self.receive(mail); } end",
            "define class Foo as private abstract receive(int x): int; end",
            "define pub class Foo as fnc read(self, int x): int { return x; } end",
            "pub define class Foo as fnc read(self, int x): int { return x; } end",
            "fnc main():int{let int x=1;return lex[x] || -> x;}",
            "fnc main():int{let int x=1;return nlex[x] || -> x;}",
            "fnc main():int{let int x=1;return nlex || -> x;}",
            "fnc main():int{let int x=1;return lex[self] || -> x;}",
            "define class Foo as fnc read(mut self, int x): int { return x; } end",
            "define class Foo as fnc read(self Foo)(int x): int { return x; } end",
            "fnc main():int{let int x=1;return lex[x,x] || -> x;}",
            "fnc main():int{let int x=1;return lex[missing] || -> x;}",
            "fnc main():int{let int x=1;return lex[] || -> x;}",
            "fnc main():int{let int x=1;return nlex[self] || -> x;}",
            "fnc main():int{let int x=1;return nlex[] || -> x;}",
            "fnc main():int{let int x=1;return lex[x] |x| -> x;}",
            "fnc main():int{let int x=1;return |y| -> x;}",
            "fnc main():int{let int x=1;return lex[x] || -> nlex || -> x;}",
            "fnc main():int{let int x=1;return nlex[self] critical |state| -> 1;}",
        })
        {
            RejectFeature(unsupported,
                unsupported.StartsWith("define class", StringComparison.Ordinal) ||
                unsupported.StartsWith("define actor", StringComparison.Ordinal) ||
                unsupported.StartsWith("define pub class", StringComparison.Ordinal) ||
                unsupported.StartsWith("pub define class", StringComparison.Ordinal)
                    ? "Nominal class/actor methods require receiver-erased method slots"
                    : "Lexical captures require checked closure lowering");
        }

        Console.WriteLine("Structured source parser and typed diagnostics passed");
    }

    private static bool IsReadOnlyButMutable<T>(this IReadOnlyList<T> list) =>
        list is T[];
}
