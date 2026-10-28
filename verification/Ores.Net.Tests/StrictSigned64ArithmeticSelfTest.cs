using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Numerics;
using Ores.Net.Runtime;

/// <summary>Reference-only arithmetic oracle, not a guest source test.</summary>
static class StrictSigned64ArithmeticSelfTest
{
    private static void Require(bool condition, string explanation)
    {
        if (!condition) throw new Exception("FAILED: " + explanation);
    }

    private static void Expect<TFailure>(Action operation, string explanation)
        where TFailure : Exception
    {
        try { operation(); }
        catch (TFailure) { return; }
        throw new Exception("Expected " + typeof(TFailure).Name + ": " + explanation);
    }

    public static void Run()
    {
        CheckPortableContractFixture();
        CheckExactFloatFixture();
        Require(StrictSigned64Arithmetic.Rem(-9, 4) == -1, "negative remainder");
        Require(StrictSigned64Arithmetic.Mod(-9, 4) == 3, "positive modulus");
        Require(StrictSigned64Arithmetic.Mod(-9, -4) == 3, "negative divisor modulus");
        Require(StrictSigned64Arithmetic.Mod(-1, long.MinValue) == long.MaxValue,
            "minimum divisor modulus");
        Require(StrictSigned64Arithmetic.Rem(long.MinValue, -1) == 0,
            "minimum remainder -1");
        Require(StrictSigned64Arithmetic.Pow(0, 0) == 1, "zero to zero");
        Require(StrictSigned64Arithmetic.Pow(-2, 63) == long.MinValue,
            "exact signed minimum exponent");
        Require(StrictSigned64Arithmetic.Pow(-1, long.MaxValue) == -1,
            "odd huge exponent");
        Require(StrictSigned64Arithmetic.BitXor(5, 3) == 6, "xor");
        Expect<DivideByZeroException>(
            () => StrictSigned64Arithmetic.Mod(1, 0), "zero divisor");
        Expect<DivideByZeroException>(
            () => StrictSigned64Arithmetic.Rem(1, 0), "zero divisor");
        Expect<OverflowException>(
            () => StrictSigned64Arithmetic.Pow(2, 63), "checked pow overflow");
        Expect<ArgumentOutOfRangeException>(
            () => StrictSigned64Arithmetic.Pow(2, -1), "negative pow exponent");

        var random = new Random(0x0A51);
        Span<byte> first = stackalloc byte[8];
        Span<byte> second = stackalloc byte[8];
        for (int i = 0; i < 10_000; i++)
        {
            random.NextBytes(first);
            random.NextBytes(second);
            long a = BinaryPrimitives.ReadInt64LittleEndian(first);
            long b = BinaryPrimitives.ReadInt64LittleEndian(second);
            if (b == 0) continue;
            BigInteger expectedRem = (BigInteger)a % b;
            BigInteger expectedMod = expectedRem < 0
                ? expectedRem + BigInteger.Abs(new BigInteger(b))
                : expectedRem;
            Require(StrictSigned64Arithmetic.Rem(a, b) == (long)expectedRem,
                "random signed remainder #" + i);
            Require(StrictSigned64Arithmetic.Mod(a, b) == (long)expectedMod,
                "random Euclidean modulus #" + i);
            Require(StrictSigned64Arithmetic.BitXor(a, b) == (a ^ b),
                "random signed XOR #" + i);
        }
    }
    private static void CheckExactFloatFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "strict-integer-to-float-exact-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Require(root.GetProperty("contract").GetString() ==
            "oreslang.strict-integer-to-float-exact.v1", "exact float contract identity");
        var cases = root.GetProperty("cases");
        Require(cases.GetArrayLength() == 11, "11 exact float vectors");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases.EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Require(ids.Add(id), "duplicate exact float vector id " + id);
            Require(item.GetProperty("operation").GetString() == "toFloatExact",
                "exact float operation identity: " + id);
            long value = Signed(item.GetProperty("operand"));
            var expected = item.GetProperty("expect");
            if (expected.TryGetProperty("error", out var error))
            {
                Require(error.GetString() == "INEXACT_CONVERSION",
                    "unexpected exact float error: " + id);
                Expect<OverflowException>(
                    () => _ = StrictSigned64Arithmetic.ToFloatExact(value), id);
                continue;
            }
            string hex = expected.GetProperty("doubleBitsHex").GetString()!;
            ulong expectedBits = ulong.Parse(hex,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            ulong actualBits = BitConverter.DoubleToUInt64Bits(
                StrictSigned64Arithmetic.ToFloatExact(value));
            Require(expectedBits == actualBits, "float bits mismatch: " + id);
        }
    }

    private static long Signed(JsonElement element) =>
        long.Parse(element.GetString()!, CultureInfo.InvariantCulture);

    private static void CheckPortableContractFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "strict-named-integer-arithmetic-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        Require(root.GetProperty("contract").GetString() ==
            "oreslang.strict-named-integer-arithmetic.v1", "contract identity");
        var cases = root.GetProperty("cases");
        Require(cases.GetArrayLength() == 26, "26 contract vectors");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in cases.EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Require(ids.Add(id), "duplicate vector id: " + id);
            long a = Signed(item.GetProperty("left"));
            long b = Signed(item.GetProperty("right"));
            string operation = item.GetProperty("operation").GetString()!;
            JsonElement expected = item.GetProperty("expect");
            Func<long> execute = operation switch
            {
                "rem" => () => StrictSigned64Arithmetic.Rem(a, b),
                "mod" => () => StrictSigned64Arithmetic.Mod(a, b),
                "pow" => () => StrictSigned64Arithmetic.Pow(a, b),
                "bit_xor" => () => StrictSigned64Arithmetic.BitXor(a, b),
                _ => throw new Exception("unknown signed64 operation: " + operation)
            };

            if (!expected.TryGetProperty("error", out var error))
            {
                Require(execute() == Signed(expected.GetProperty("value")),
                    "incorrect signed64 result: " + id);
                continue;
            }
            switch (error.GetString())
            {
                case "DIVISOR_ZERO":
                    Expect<DivideByZeroException>(() => _ = execute(), id);
                    break;
                case "OVERFLOW":
                    Expect<OverflowException>(() => _ = execute(), id);
                    break;
                case "NEGATIVE_EXPONENT":
                    Expect<ArgumentOutOfRangeException>(() => _ = execute(), id);
                    break;
                default:
                    throw new Exception("unrecognized signed64 failure contract " + id);
            }
        }
    }

}
