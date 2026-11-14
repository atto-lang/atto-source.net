using Ores.Net.Compiler;
using Mono.Cecil;

static class CrossBackendCheckedI64ParityTests
{
    private const string Header = "// oreslang:checked-i64-v1\n";

    public static void Run()
    {
        PercentOperatorIsRejectedAndInternalRemainderRemainsBounded();
        NativeI64EffectResultCannotForgeSuccess();
        foreach (var (name, expression, expected) in new (string, string, long)[]
        {
            ("precedence", "2 + 3 * 4", 14L),
            ("parentheses", "(2 + 3) * 4", 20L),
            ("negative", "-7 + 3", -4L),
            ("max", "9223372036854775807", long.MaxValue),
            ("min", "-9223372036854775808", long.MinValue),
            ("zero", "0", 0L),
            ("signed multiplication", "-7 * -6", 42L),
        })
        {
            var ast = OresParser.Parse(Header + "define pub fnc main(): int { return " + expression + "; }");
            var ir = OresIrLowering.Lower(ast);
            var actual = OresIrReferenceEvaluator.Evaluate(ir);
            if (actual != expected)
                throw new Exception($"FAILED: cross-backend checked-i64 {name}: expected {expected}, got {actual}");
        }

        foreach (var (name, expression) in new (string, string)[]
        {
            ("add overflow", "9223372036854775807 + 1"),
            ("subtract overflow", "-9223372036854775808 - 1"),
            ("multiply overflow", "9223372036854775807 * 2"),
            ("unary negate minimum", "-(-9223372036854775808)"),
        })
        {
            var ast = OresParser.Parse(
                Header + "define pub fnc main(): int { return " + expression + "; }");
            var ir = OresIrLowering.Lower(ast);
            RequireOverflowEffect(OresIrReferenceEvaluator.EvaluateEffect(ir),
                name + " OresIR");

            var folder = Path.Combine(Path.GetTempPath(), "oresnet-overflow-" +
                Guid.NewGuid().ToString("N"));
            var file = Path.Combine(folder, "OresGenerated.dll");
            try
            {
                CilEmitter.EmitLibrary(ir, file);
                using var assembly = AssemblyDefinition.ReadAssembly(file);
                var method = assembly.MainModule.Types
                    .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                    .Methods.Single(f => f.Name == "Run");
                RequireOverflowEffect(CilReferenceEvaluator.EvaluateEffect(method),
                    name + " emitted CIL");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        try
        {
            _ = OresParser.Parse(Header + "define pub fnc main(): int { return 7 / 2; }");
        }
        catch (FormatException ex) when (ex.Message.Contains("float result lowering", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("FAILED: int/int division was admitted without float lowering");
    }

    private static void NativeI64EffectResultCannotForgeSuccess()
    {
        const uint overflow = (uint)NumericGuestErrorCode.ArithmeticOverflowError;
        var thrown = GuestI64EffectResult.ArithmeticOverflow();
        if (thrown.IsReturn || !thrown.CatchMatches(overflow) ||
            thrown.Forward().Value != 0 ||
            thrown.Recover(overflow, 42).Value != 42 ||
            !thrown.Recover(overflow, 42).IsReturn)
            throw new Exception("Guest overflow lost its exact catchable identity");

        var unrelated = thrown.Recover(65538u, 42);
        if (unrelated.IsReturn || unrelated.Value != 0 ||
            unrelated.Status.Code != overflow)
            throw new Exception("Wrong guest Error class swallowed overflow");

        // Guest code zero is reserved for Return and must never be
        // synthesized into a catchable Error by a malformed native slot.
        var forgedZeroCodeThrow = new GuestI64EffectResult(
            99, new GuestEffectStatus(GuestEffectKind.Throw, 0));
        var zeroCodeForwarded = forgedZeroCodeThrow.Forward().Forward();
        if (forgedZeroCodeThrow.IsReturn ||
            forgedZeroCodeThrow.CatchMatches(0) ||
            zeroCodeForwarded.IsReturn || zeroCodeForwarded.Value != 0 ||
            zeroCodeForwarded.Status.Kind != GuestEffectKind.Fatal ||
            zeroCodeForwarded.Status.Code != 0xFFFFFFFDu)
            throw new Exception("Guest Throw with success code zero was admitted");

        var forgedZeroCodeTransport = new GuestEffectTransportException(
            new GuestEffectStatus(GuestEffectKind.Throw, 0),
            "ArithmeticOverflowError", "forged zero-code throw");
        if (forgedZeroCodeTransport.Status.Kind != GuestEffectKind.Fatal ||
            forgedZeroCodeTransport.Status.Code != 0xFFFFFFFDu)
            throw new Exception("CLR transport admitted guest Throw with code zero");

        var forgedReturn = new GuestI64EffectResult(
            42, new GuestEffectStatus(GuestEffectKind.Return, overflow));
        if (forgedReturn.IsReturn || forgedReturn.CatchMatches(overflow))
            throw new Exception("Return+error masqueraded as success/catchable throw");
        var fenced = forgedReturn.Forward().Forward();
        if (fenced.IsReturn || fenced.Value != 0 ||
            fenced.Status.Kind != GuestEffectKind.Fatal ||
            fenced.Status.Code != 0xFFFFFFFDu)
            throw new Exception("Forged Return+error did not become fatal");

        var transport = new GuestEffectTransportException(
            new GuestEffectStatus(GuestEffectKind.Return, overflow),
            "ArithmeticOverflowError", "forged Return+error");
        if (transport.Status.Kind != GuestEffectKind.Fatal ||
            transport.Status.Code != 0xFFFFFFFDu)
            throw new Exception("CLR transport failed to normalize forged guest return");

        // All eight numeric guest error identities must pass through the
        // exact typed result carrier without being renamed or caught by an
        // unrelated handler. This is ABI evidence, not NativeAOT guest proof.
        for (uint numericCode = 65537; numericCode <= 65544; numericCode++)
        {
            var original = new GuestI64EffectResult(
                99, new GuestEffectStatus(GuestEffectKind.Throw, numericCode));
            var forwarded = original.Forward().Forward();
            if (forwarded.IsReturn || forwarded.Value != 0 ||
                forwarded.Status.Kind != GuestEffectKind.Throw ||
                forwarded.Status.Code != numericCode ||
                !forwarded.CatchMatches(numericCode) ||
                forwarded.CatchMatches(numericCode == 65544 ? 65537u : numericCode + 1))
                throw new Exception("Numeric guest error ABI identity drift: " + numericCode);
            var recovered = forwarded.Recover(numericCode, 42);
            if (!recovered.IsReturn || recovered.Value != 42 ||
                recovered.Status.Code != 0)
                throw new Exception("Exact numeric error catch did not recover: " + numericCode);
        }

        foreach (GuestEffectKind terminal in new[] {
            GuestEffectKind.Raise, GuestEffectKind.Panic, GuestEffectKind.Cancel,
            GuestEffectKind.Fatal, GuestEffectKind.ProcessExit
        })
        {
            var incoming = new GuestI64EffectResult(
                99, new GuestEffectStatus(terminal, overflow));
            var outgoing = incoming.Recover(overflow, 42).Forward();
            if (outgoing.IsReturn || outgoing.Value != 0 ||
                outgoing.Status.Kind != terminal ||
                outgoing.Status.Code != overflow)
                throw new Exception("Terminal guest effect was swallowed: " + terminal);
        }

        for (int raw = 7; raw <= 255; raw++)
        {
            var forged = new GuestI64EffectResult(
                99, new GuestEffectStatus((GuestEffectKind)raw, overflow));
            var forwarded = forged.Forward().Forward();
            if (forwarded.IsReturn || forwarded.Value != 0 ||
                forwarded.Status.Kind != GuestEffectKind.Fatal ||
                forwarded.Status.Code != 0xFFFFFFFDu ||
                forged.CatchMatches(overflow))
                throw new Exception("Invalid effect byte was accepted: " + raw);
        }
    }

    private static void RequireOverflowEffect(
        GuestI64EffectResult result, string where)
    {
        if (result.Status.Kind != GuestEffectKind.Throw ||
            result.Status.Code !=
                (uint)NumericGuestErrorCode.ArithmeticOverflowError)
            throw new Exception(
                $"FAILED: {where}: expected Throw/ArithmeticOverflowError(65537), " +
                $"got {result.Status.Kind}/{result.Status.Code}");
    }

    private static void PercentOperatorIsRejectedAndInternalRemainderRemainsBounded()
    {
        // Guest source syntax never admits '%'; future named Mod(a,b) lowering
        // may reuse the bounded internal remainder opcode, but no host/CIL
        // remainder expression is a substitute for the language API.
        foreach (string expression in new[]
        {
            "-7 % 3", "7 % -3", "-7 % -3", "8 % 3 * 2",
            "20 % 6 % 3", "-9223372036854775808 % -1",
            "-9223372036854775808 % -9223372036854775808",
            "9223372036854775807 % -9223372036854775808",
            "7 % 0", "7 % (1+1)", "7 % -(1+1)",
            "7 % -9223372036854775809"
        })
        {
            try
            {
                _ = OresParser.Parse(Header +
                    "pub fnc main():int{return " + expression + ";}");
                throw new Exception("Compiler admitted removed % source operator: " + expression);
            }
            catch (FormatException ex)
            {
                if (!ex.Message.Contains("The % operator is forbidden", StringComparison.Ordinal))
                    throw new Exception("Unexpected % rejection diagnostic: " + ex.Message);
            }
        }

        // Retain a bounded verifier/CIL regression for the internal operation,
        // which has no Oreslang '%' source spelling and no claimed Mod API yet.
        var internalIr = OresIrVerifier.Verify(new IrFunction(
            "main", IrValueType.I64, new[]
            {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: -7),
                new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: 3),
                new IrInstruction(2, IrOpcode.SignedRemainderLiteral, IrValueType.I64,
                    Left: 0, Right: 1)
            }, 2));
        if (OresIrReferenceEvaluator.Evaluate(internalIr) != -1)
            throw new Exception("Internal remainder reference drift");
        var folder = Path.Combine(Path.GetTempPath(), "oresnet-internal-rem-" +
            Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "OresGenerated.dll");
        try
        {
            CilEmitter.EmitLibrary(internalIr, file);
            using var assembly = AssemblyDefinition.ReadAssembly(file);
            var method = assembly.MainModule.Types
                .Single(t => t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            if (CilReferenceEvaluator.Evaluate(method) != -1)
                throw new Exception("Internal remainder CIL drift");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        // The IR is untrusted; a source parser's admission does not grant
        // externally constructed IR the right to use arbitrary divisors.
        foreach (long divisor in new long[] { 0, -1 })
        {
            var operations = new[]
            {
                new IrInstruction(0, IrOpcode.ConstantI64, IrValueType.I64, Constant: 7),
                divisor == 0
                    ? new IrInstruction(1, IrOpcode.ConstantI64, IrValueType.I64, Constant: 0)
                    : new IrInstruction(1, IrOpcode.CheckedSubtract, IrValueType.I64,
                        Left: 0, Right: 0),
                new IrInstruction(2, IrOpcode.SignedRemainderLiteral, IrValueType.I64,
                    Left: 0, Right: 1)
            };
            try
            {
                _ = OresIrVerifier.Verify(new IrFunction("main", IrValueType.I64, operations, 2));
            }
            catch (FormatException)
            {
                continue;
            }
            throw new Exception("Untrusted remainder IR escaped literal divisor gate");
        }
    }
}
