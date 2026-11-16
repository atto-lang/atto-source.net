using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Ores.Net.Compiler;

/// <summary>
/// Exact 24-source Atto Rust/Wasm PR #219 ADT guard corpus.
///
/// Deliberately reports unsupported-source diagnostics separately from
/// genuine semantic negatives and real emitted CIL guest results. This gate
/// must NEVER be interpreted as proof of .NET tagged Result/match parity.
/// </summary>
internal static class AdtGuardParityAdmissionTests
{
    private static readonly (string Name, string Blob)[] Cases =
    [
        ("fail/adt-enum-missing-variant.atto", "57499a8f7f346f9ea16a03a8f71eab5cb8ab3546"),
        ("fail/adt-enum-other-family-arm.atto", "377cd5925554ca0d2dcdc1ac0eba57579fb1804a"),
        ("fail/adt-guard-must-be-bool.atto", "ead6b189d8ff854c3e2bff6bb4be94548343f989"),
        ("fail/adt-nested-wrong-success-payload.atto", "2caff7da4095d30cbfd9e7f4b9be3512cbe505a7"),
        ("fail/adt-option-bool-unsupported.atto", "f2f6e7d0a060726123f0cc003ee36dad50c9b999"),
        ("fail/adt-option-duplicate-unguarded.atto", "325f9f42ee699524bb83ba597bba2c06f15def7c"),
        ("fail/adt-option-guard-not-exhaustive.atto", "8b416255ebdefb9841d5debab4301ad1e19d5673"),
        ("fail/adt-option-wrong-family-pattern.atto", "f4ab0066e858d7c346ce11ccd7d63e5e3d224b08"),
        ("fail/adt-option-wrong-payload-type.atto", "f121a168954a9624f632c19b3139926910e6bc24"),
        ("fail/adt-result-int-error-unsupported.atto", "2438ae98a587d8e0d73711148c9e31a5420a3bab"),
        ("fail/adt-result-wrong-error-type.atto", "d8e04f029d44279e1750282ecd6c5af892f5e864"),
        ("fail/adt-result-wrong-family-pattern.atto", "65bd7797f0480cba910c0478474554133601e461"),
        ("pass/adt-enum-three-way-exhaustive.atto", "2cda216dd4c2dbfce20bdb6d9af0a0cd8cc28415"),
        ("pass/adt-err-guard-fallthrough.atto", "5c882ca6263885d644e8a54cf50ff67a0cbf275c"),
        ("pass/adt-guard-skips-none-payload.atto", "90a5c0beb06ec6f5787ca67d66f555eb14784386"),
        ("pass/adt-guard-skips-wrong-result-tag.atto", "4730792d63852a885c62642c2f79facf8956ad04"),
        ("pass/adt-match-subject-once-guard.atto", "ab3cde2ce9145d07f1403e4d6a06aeb1bd44e6eb"),
        ("pass/adt-nested-err-guard-no-flatten.atto", "03ed7876c3196f53e3dae905b098d0d57b3a014f"),
        ("pass/adt-nested-guard-typed-payload.atto", "0268d620f32d822689f887ea5cb425d356370531"),
        ("pass/adt-option-guard-fallthrough.atto", "8e6f7bf4ba3bd98822c2e7d54395c337372ab914"),
        ("pass/adt-option-guard-hit.atto", "7fb872ba97236df6e1a6b83df55f215665486fad"),
        ("pass/adt-result-err-guard.atto", "c356b6dced659610db9b60eabebca86f4b4aae10"),
        ("pass/adt-result-ok-guard.atto", "ee40cd523d2def9352ad43b8661949334756a9b3"),
        ("pass/adt-whole-value-guard-binding.atto", "4bffdd8d9b828acb907988495e4ef303aa384d2e")
    ];

    public static void Run()
    {
        if (Cases.Length != 24)
            throw new Exception("ADT parity fixture manifest is incomplete");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int positivesExecuted = 0, unsupportedPositives = 0, negativeRejections = 0;
        foreach (var (name, expectedBlob) in Cases)
        {
            if (!ids.Add(name) || name.Contains("..", StringComparison.Ordinal) ||
                !(name.StartsWith("pass/", StringComparison.Ordinal) ||
                  name.StartsWith("fail/", StringComparison.Ordinal)))
                throw new Exception("Unsafe or duplicate ADT fixture path");
            string file = Path.Combine(AppContext.BaseDirectory, "adt-guard-v1",
                name.Replace('/', Path.DirectorySeparatorChar));
            string text;
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0 || bytes.Length > 1_048_576)
                throw new Exception("ADT fixture exceeded source budget: " + name);
            byte[] prefix = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
            byte[] objectBytes = new byte[prefix.Length + bytes.Length];
            Buffer.BlockCopy(prefix, 0, objectBytes, 0, prefix.Length);
            Buffer.BlockCopy(bytes, 0, objectBytes, prefix.Length, bytes.Length);
            string actualBlob = Convert.ToHexString(SHA1.HashData(objectBytes))
                .ToLowerInvariant();
            if (actualBlob != expectedBlob)
                throw new Exception("ADT canonical Git blob drift: " + name);
            text = new UTF8Encoding(false, true).GetString(bytes);
            bool positive = name.StartsWith("pass/", StringComparison.Ordinal);
            if (!text.Contains(positive ? "// expect-exit:" : "// expect-error:",
                StringComparison.Ordinal))
                throw new Exception("ADT fixture missing its original expectation: " + name);

            VerifiedCfgModule? module;
            try
            {
                module = OresStructuredCfgLowering.LowerModule(text);
            }
            catch (FormatException ex)
            {
                // A positive that is not compiled is NOT a parity pass.
                // A negative rejected by the lexer is NOT semantic proof.
                if (positive) unsupportedPositives++;
                else negativeRejections++;
                Console.WriteLine("NET_ADT_GUARD_CASE=" + name +
                    "|state=UNSUPPORTED_OR_REJECTED_NOT_SEMANTICALLY_CERTIFIED" +
                    "|diagnostic=" + ex.Message.Replace('\n', ' '));
                continue;
            }
            if (!positive)
                throw new Exception("Invalid ADT source admitted to verified .NET CFG: " + name);

            // If source support arrives, the positive must execute the real
            // verified CFG and freshly emitted CIL before counting as passed.
            var match = Regex.Match(text, @"(?m)^// expect-exit:\s*(-?\d+)\s*$");
            if (!match.Success)
                throw new Exception("ADT positive has no expected signed i64 exit: " + name);
            long expected = long.Parse(match.Groups[1].Value,
                CultureInfo.InvariantCulture);
            if (CfgReferenceEvaluator.Evaluate(module.Main) != expected)
                throw new Exception("ADT verified CFG runtime mismatch: " + name);
            string dir = Path.Combine(Path.GetTempPath(),
                "atto-adt-guard-cil-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string dll = Path.Combine(dir, "OresGenerated.dll");
                CilEmitter.EmitLibrary(module, dll);
                using var assembly = AssemblyDefinition.ReadAssembly(dll);
                var guest = assembly.MainModule.Types.Single(type =>
                    type.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(method => method.Name == "Run");
                if (CfgCilTestInterpreter.Evaluate(guest) != expected)
                    throw new Exception("ADT emitted CIL guest mismatch: " + name);
            }
            finally { Directory.Delete(dir, true); }
            positivesExecuted++;
            Console.WriteLine("NET_ADT_GUARD_CASE=" + name +
                "|state=VERIFIED_CFG_AND_EMITTED_CIL_GUEST");
        }
        Console.WriteLine($"NET_ADT_GUARD_ADMISSION_DONE cases={Cases.Length}" +
            $" positive_cil_executed={positivesExecuted}" +
            $" positives_unsupported={unsupportedPositives}" +
            $" negative_rejected_not_certified={negativeRejections}" +
            " parity=NOT_CERTIFIED");
    }
}
