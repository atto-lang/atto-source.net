using Ores.Net.Compiler;

internal static class SafeCollectionSourceAdmissionTests
{
    private const string Code = "E_SAFE_COLLECTION_ACCESS_UNSUPPORTED";

    private static void MustDeny(Action compile, string name, int offset)
    {
        try
        {
            compile();
        }
        catch (FormatException error) when
            (error.Message.Contains(Code, StringComparison.Ordinal) &&
             error.Message.Contains($"at offset {offset}", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Unverified collection source was not rejected with a precise compiler diagnostic: " + name);
    }

    public static void Run()
    {
        // Exercise the actual shipping parser/typed OresIR and the opt-in
        // structured parser/verified CFG. This is an unsupported-feature gate,
        // NEVER proof that .NET has guest arrays, Option or borrow checking.
        var negative = new (string Name, string Source, char Marker)[]
        {
            ("array-literal", "fnc main():int{return [1,2,3][0];}", '['),
            ("direct-read", "fnc main():int{return values[0];}", '['),
            ("direct-write", "fnc main():int{values[0]=9;return 0;}", '['),
            ("negative-index", "fnc main():int{return values[-1];}", '['),
            ("out-of-range", "fnc main():int{return values[9223372036854775807];}", '['),
            ("array-type", "fnc main():int{val Array[int] values=[1];return 1;}", '['),
            ("nested-rows", "fnc main():int{return rows[0][1];}", '['),
            ("slice", "fnc main():int{return values[0:3];}", '['),
            ("borrowed-element", "fnc main():int{let loan=rt borrow values[0];return 1;}", '['),
            ("mutable-loan", "fnc main():int{let loan=rt mut borrow values[0];return 1;}", '['),
            ("unpaired-close", "fnc main():int{return 1];}", ']')
        };
        foreach (var (name, source, marker) in negative)
        {
            int at = source.IndexOf(marker);
            if (at < 0) throw new Exception("Broken collection test fixture " + name);
            MustDeny(() => _ = OresIrLowering.Lower(OresParser.Parse(source)),
                name + "/shipping-typed-ir", at);
            MustDeny(() => _ = OresStructuredCfgLowering.Lower(source),
                name + "/structured-verified-cfg", at);
        }

        // Comments and identifier substrings must never mint collection syntax.
        var inert = new[]
        {
            "fnc main():int{/* [999] /* nested [0] */ ignored */return 42;}",
            "fnc main():int{// values[-1]\nreturn 42;}",
            "fnc main():int{return 42;} // [unbalanced",
            "fnc main():int{/* ] */return 42;}"
        };
        foreach (var source in inert)
        {
            _ = OresIrLowering.Lower(OresParser.Parse(source));
            _ = OresStructuredCfgLowering.Lower(source);
        }
        _ = OresStructuredCfgLowering.Lower(
            "fnc main():int{let int index_value=40;return index_value+2;}");

        // The existing scalar compiler still runs and verifies an executable
        // numeric control through its normal typed source path.
        var scalar = OresIrLowering.Lower(OresParser.Parse(
            "fnc main():int{return (5+3)*7-14;}"));
        if (OresIrReferenceEvaluator.Evaluate(scalar) != 42)
            throw new Exception("Safe-collection gate regressed the shipping scalar compiler");
        _ = OresStructuredCfgLowering.Lower(
            "fnc main():int{let int value=40;return value+2;}");

        // Borrow/Option source must not be misreported as a successful Rust-
        // style compile-time ownership/Option implementation. Existing gates
        // must reject until tagged-sum and region-aware lowering is available.
        foreach (var unlowered in new[]
        {
            "fnc main():Option<int>{return Some(42);}",
            "fnc main():int{let int v=42;let loan=rt borrow v;return 42;}",
            "fnc main():int{let int v=42;let loan=rt mut borrow v;return 42;}"
        })
        {
            static bool Denied(Func<object> parse)
            {
                try { _ = parse(); }
                catch (FormatException) { return true; }
                catch (NotSupportedException) { return true; }
                return false;
            }
            if (!Denied(() => OresParser.Parse(unlowered)) ||
                !Denied(() => OresStructuredParser.Parse(unlowered)))
                throw new Exception("Unverified Option/borrow source was admitted");
        }
        Console.WriteLine("SAFE_COLLECTION_ADMISSION_V1|indexed_rejections=" +
            negative.Length + "|frontends=2|guest_option_and_borrow=NOT_IMPLEMENTED");
    }
}
