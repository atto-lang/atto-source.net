using Ores.Net.Compiler;

internal static class HeapContainerSourceBoundaryTests
{
    // Real scalar OresParser -> typed IR boundary; no Box/Arc/Vector CIL
    // lowering is claimed. Do not count the host .NET heap as guest support.
    public static void Run()
    {
        var scalar = OresIrLowering.Lower(
            OresParser.Parse("pub fnc main():int{return 21+21;}"));
        if (OresIrReferenceEvaluator.Evaluate(scalar) != 42)
            throw new Exception("heap-container boundary scalar positive control failed");

        // This is genuine AttoLang source lowered by OresParser into the
        // checked scalar OresIR and emitted as CIL. A Tuple value is permitted
        // only when an immediate constant-index projection proves it cannot
        // escape: no unverified owning heap representation is manufactured.
        // Tuple constant-index projections (`Tuple.of(..)[i]`) stay out: main's
        // SafeCollectionSourceAdmission rejects every bracket until checked
        // bounds / Option results / ref32 lowering exists.
        var closedProducts = new (string Source, long Expected)[]
        {
            ("pub fnc main():int{return Record.of(low: 11, high: 23).high + 19;}", 42),
            ("pub fnc main():int{return ~record(low: 11, high: 23).high + 19;}", 42),
            ("pub fnc main():int{return Record.of(a: -9223372036854775808, b: 42).b;}", 42),
            ("pub fnc main():int{return Entry.of(11, 23).value + 19;}", 42),
            ("pub fnc main():int{return ~entry(11, 23).key + 31;}", 42),
            ("pub fnc main():int{return Pair.of(11, 23).second + 19;}", 42),
            ("pub fnc main():int{return ~pair(11, 23).first + 31;}", 42),
            ("pub fnc main():int{return Entry.of<int,int>(-9223372036854775808, 42).value;}", 42),
            ("pub fnc main():int{return Pair.of<int,int>(11, 23).first + 31;}", 42)
        };
        string folder = Path.Combine(Path.GetTempPath(),
            "atto-tuple-scalar-cil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (int i = 0; i < closedProducts.Length; i++)
            {
                var (source, expected) = closedProducts[i];
                var nativeIr = OresIrLowering.Lower(OresParser.Parse(source));
                if (OresIrReferenceEvaluator.Evaluate(nativeIr) != expected)
                    throw new Exception("Native scalarized tuple OresIR returned wrong result");
                string output = Path.Combine(folder, "tuple-" + i + ".dll");
                CilEmitter.EmitLibrary(nativeIr, output);
                using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(output);
                var entry = assembly.MainModule.Types.Single(t =>
                    t.FullName == "Oreslang.Generated.OresProgram").Methods.Single(
                        m => m.Name == "Run");
                if (CilReferenceEvaluator.Evaluate(entry) != expected)
                    throw new Exception("Native scalarized tuple CIL returned wrong result");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        var forbiddenFactories = new[]
        {
            "Tuple.of(1,2)",           // cannot escape as a first-class value
            "Tuple.of(1,2)[2]",        // exact arity and bounds
            "Tuple.of(1,2)[-1]",       // checked nonnegative slot only
            "Tuple.of(1+2,3)[0]",     // not statically proven side-effect-free literal
            "Tuple.of<int>(1,2)[0]",  // wrong witness arity
            "Tuple.of(true,3)[0]",    // scalar i64-only type gate
            "Tuple.of(1,2)[0].foo",   // no dynamic guest object member
            "Map.of<string,int>()",   // owned Map remains unsupported
            "Entry.of(1,2)",      // first-class nominal Entry cannot escape
            "Pair.of(1,2)",       // first-class nominal Pair cannot escape
            "Entry.of(1,2).first", // Entry and Pair projections remain distinct
            "Pair.of(1,2).key",
            "~entry(1,2).missing",
            "~pair(1,2).value",
            "Entry.of(1,2).key.foo", // no dynamic member fallback
            "Pair.of(1,true).second",
            "Entry.of<int>(1,2).key", // both type slots required, int-only
            "~entry(1+2,3).key", // no unproven effects
            "Array.of(1,2)",
            "List.of(1,2)",
            "Vector.of(1,2)",
            "Set.of(1,2)",
            "Record.of(ready: true)",  // bool values not in i64 scalar-only subset
            "Record.of(x: 1)",         // record cannot escape
            "Record.of(x: 1).y",       // no unknown/prototype-backed field
            "Record.of(x: 1, x: 2).x", // duplicate static field
            "Record.of(x: 1+1).x",     // non-pure nonliteral initializer
            "Record.of(x: 1).x.foo",   // no dynamic member dispatch
            "~record(x: 1).missing",
            "~map(1 => 2)",
            "~array(1,2)"
        };
        foreach (var factory in forbiddenFactories)
        {
            string source = "pub fnc main():int{return " + factory + ";}";
            try
            {
                _ = OresIrLowering.Lower(OresParser.Parse(source));
                throw new Exception("Unverified native collection factory emitted IR: " + factory);
            }
            catch (FormatException) { }
        }
        // Structured frontend must not reinterpret a nominal product or
        // dynamic collection as a scalar helper call.
        foreach (string factory in new[] {
            "Tuple.of(11,23)[1]", "Record.of(x:11).x",
            "Entry.of(11,23).key", "Pair.of(11,23).first",
            "Map.of()", "~tuple(11,23)[0]", "~record(x:11).x", "~array(11,23)",
            "~entry(11,23).value", "~pair(11,23).second"
        })
        {
            try
            {
                _ = OresStructuredParser.Parse(
                    "pub fnc main():int{return " + factory + ";}");
                throw new Exception("Unverified structured collection admitted: " + factory);
            }
            // Bracket and `~` aggregate forms are refused even earlier by main's
            // fail-closed lexer admissions; any of these diagnostics proves rejection.
            catch (FormatException ex) when
                (ex.Message.Contains("E_COLLECTION_FACTORY_UNSUPPORTED", StringComparison.Ordinal) ||
                 ex.Message.Contains("E_SAFE_COLLECTION_ACCESS_UNSUPPORTED", StringComparison.Ordinal) ||
                 ex.Message.Contains("TILDE_AGGREGATE_BACKEND_UNSUPPORTED", StringComparison.Ordinal))
            { }
        }

        var unsupported = new (string Name, string Source)[]
        {
            ("box-owner", "pub fnc main():int{val Box<int> b=Box<int>(42);return 42;}"),
            ("atomic-arc", "pub fnc main():int{val Arc<int> a=Arc<int>(42);return 42;}"),
            ("weak-arc", "pub fnc main():int{val Weak<int> w=null;return 42;}"),
            ("resizable-vector", "pub fnc main():int{val Vector[int] v=[42];return 42;}"),
            ("resizable-array", "pub fnc main():int{val Array<int> v=new Array<int>();v.add(42);return 42;}"),
            ("heap-move", "pub fnc main():int{let int x=42;let moved=rt take x;return x;}"),
            ("shared-loan", "pub fnc main():int{let int x=42;let ref=rt borrow x;return 42;}"),
            ("mutable-loan", "pub fnc main():int{let int x=42;let ref=rt mut borrow x;return 42;}"),
            ("unguarded-mut-share", "pub fnc main():int{let int x=42;rt mut share x;return 42;}"),
            ("mutable-container", "define class Counter as state{} end pub fnc main():int{return 42;}")
        };
        foreach (var (name, source) in unsupported)
        {
            try
            {
                // If parsing succeeds, lowering must also fail: otherwise the
                // scalar CIL compiler may silently erase unproved heap effects.
                var typed = OresIrLowering.Lower(OresParser.Parse(source));
                throw new Exception("Unverified " + name +
                    " emitted typed IR: " + typed.GetType().Name);
            }
            catch (FormatException ex)
            {
                if (string.IsNullOrWhiteSpace(ex.Message))
                    throw new Exception("Missing compiler-owned diagnostic for " + name);
            }
            catch (NotSupportedException ex)
            {
                if (string.IsNullOrWhiteSpace(ex.Message))
                    throw new Exception("Missing unsupported diagnostic for " + name);
            }
        }
    }
}
