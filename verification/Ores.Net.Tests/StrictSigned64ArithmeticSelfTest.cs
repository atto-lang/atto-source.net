using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
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
        CheckMathUnaryPrecisionFixture();
        CheckBigIntExactFixture();
        CheckDecimalPrecisionContractIdentity();
        CheckPortableContractFixture();
        CheckExactFloatFixture();
        CheckExactFloatOracleBoundaries();
        Require(StrictSigned64Arithmetic.Neg(0) == 0, "zero negation");
        Require(StrictSigned64Arithmetic.Neg(42) == -42, "positive negation");
        Require(StrictSigned64Arithmetic.Neg(-42) == 42, "negative negation");
        Require(StrictSigned64Arithmetic.Abs(0) == 0, "zero absolute value");
        Require(StrictSigned64Arithmetic.Abs(-42) == 42, "negative absolute value");
        Require(StrictSigned64Arithmetic.Abs(long.MaxValue) == long.MaxValue,
            "maximum absolute value");
        Expect<OverflowException>(
            () => StrictSigned64Arithmetic.Neg(long.MinValue), "minimum negation overflow");
        Expect<OverflowException>(
            () => StrictSigned64Arithmetic.Abs(long.MinValue), "minimum absolute overflow");
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
            if (a == long.MinValue)
            {
                Expect<OverflowException>(() => StrictSigned64Arithmetic.Neg(a),
                    "random minimum negation");
                Expect<OverflowException>(() => StrictSigned64Arithmetic.Abs(a),
                    "random minimum absolute");
            }
            else
            {
                Require(StrictSigned64Arithmetic.Neg(a) == -a,
                    "random signed negation #" + i);
                Require(StrictSigned64Arithmetic.Abs(a) == Math.Abs(a),
                    "random signed absolute #" + i);
            }
            Require(StrictSigned64Arithmetic.Rem(a, b) == (long)expectedRem,
                "random signed remainder #" + i);
            Require(StrictSigned64Arithmetic.Mod(a, b) == (long)expectedMod,
                "random Euclidean modulus #" + i);
            Require(StrictSigned64Arithmetic.BitXor(a, b) == (a ^ b),
                "random signed XOR #" + i);
        }
    }
    private static void CheckMathUnaryPrecisionFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "native-math-unary-precision-v1.json");
        byte[] bytes = File.ReadAllBytes(path);
        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        byte[] gitObject = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, gitObject, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, gitObject, header.Length, bytes.Length);
        string blob = Convert.ToHexString(SHA1.HashData(gitObject)).ToLowerInvariant();
        Require(blob == "3249d837ae6bcf6e2c2c23cac872becf92d0c8e0",
            "native Math contract blob identity");

        using var document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Require(root.GetProperty("contract").GetString() ==
            "oreslang.native-math-unary-precision.v1", "native Math contract identity");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in root.GetProperty("signed_i64_unary").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Require(seen.Add(id), "duplicate Math unary id " + id);
            string operation = item.GetProperty("operation").GetString()!;
            long value = Signed(item.GetProperty("operand"));
            Func<long> execute = operation switch
            {
                "Math.abs" => () => StrictSigned64Arithmetic.Abs(value),
                "Math.neg" => () => StrictSigned64Arithmetic.Neg(value),
                _ => throw new Exception("unknown Math unary operation " + operation)
            };
            JsonElement expected = item.GetProperty("expect");
            if (expected.TryGetProperty("error", out JsonElement error))
            {
                Require(error.GetString() == "OVERFLOW", "unexpected Math unary error " + id);
                Expect<OverflowException>(() => _ = execute(), id);
            }
            else
            {
                Require(execute() == Signed(expected.GetProperty("value")),
                    "Math unary value mismatch " + id);
            }
        }

        foreach (JsonElement item in root.GetProperty("bigint_unary").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Require(seen.Add(id), "duplicate BigInt unary id " + id);
            BigInteger value = BigInteger.Parse(item.GetProperty("operand").GetString()!,
                CultureInfo.InvariantCulture);
            BigInteger actual = item.GetProperty("operation").GetString() == "Math.neg"
                ? BigInteger.Negate(value) : BigInteger.Abs(value);
            BigInteger expected = BigInteger.Parse(item.GetProperty("expect").GetString()!,
                CultureInfo.InvariantCulture);
            Require(actual == expected, "BigInt unary reference mismatch " + id);
        }

        foreach (JsonElement item in root.GetProperty("decimal_unary").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Require(seen.Add(id), "duplicate Decimal unary id " + id);
            decimal value = decimal.Parse(item.GetProperty("operand").GetString()!,
                NumberStyles.Number, CultureInfo.InvariantCulture);
            decimal actual = item.GetProperty("operation").GetString() == "Math.neg"
                ? -value : Math.Abs(value);
            decimal expected = decimal.Parse(item.GetProperty("expect").GetString()!,
                NumberStyles.Number, CultureInfo.InvariantCulture);
            Require(actual == expected, "Decimal unary reference mismatch " + id);
            int scale = (decimal.GetBits(actual)[3] >> 16) & 0x7f;
            Require(scale == item.GetProperty("scale").GetInt32(),
                "Decimal scale mismatch " + id);
        }

        foreach (JsonElement item in root.GetProperty("binary64_unary").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Require(seen.Add(id), "duplicate binary64 unary id " + id);
            ulong inputBits = ulong.Parse(item.GetProperty("operand_bits").GetString()!,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            double value = BitConverter.Int64BitsToDouble(unchecked((long)inputBits));
            double actual = item.GetProperty("operation").GetString() == "Math.abs"
                ? Math.Abs(value) : -value;
            ulong expectedBits = ulong.Parse(item.GetProperty("expect_bits").GetString()!,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            Require(BitConverter.DoubleToUInt64Bits(actual) == expectedBits,
                "binary64 unary bits mismatch " + id);
        }
    }

    private static void CheckBigIntExactFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "bigint-exact-v1.json");
        byte[] bytes = File.ReadAllBytes(path);
        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        byte[] gitObject = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, gitObject, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, gitObject, header.Length, bytes.Length);
        string blob = Convert.ToHexString(SHA1.HashData(gitObject)).ToLowerInvariant();
        Require(blob == "b49c7a7d9395f7298e5534c4a6e5b90d4a2a518c",
            "exact BigInt contract blob identity");

        using var document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Require(root.GetProperty("contract").GetString() == "oreslang.bigint-exact.v1",
            "exact BigInt contract identity");

        foreach (JsonElement item in root.GetProperty("division_cases").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            BigInteger a = BigInteger.Parse(item.GetProperty("left").GetString()!,
                CultureInfo.InvariantCulture);
            BigInteger b = BigInteger.Parse(item.GetProperty("right").GetString()!,
                CultureInfo.InvariantCulture);
            if (item.TryGetProperty("error", out JsonElement error))
            {
                Require(error.GetString() == "DIVISOR_ZERO" && b.IsZero,
                    "BigInt divisor-zero reference " + id);
                continue;
            }
            BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
            Require(q.ToString(CultureInfo.InvariantCulture) ==
                item.GetProperty("quot").GetString(), "BigInt quotient " + id);
            Require(r.ToString(CultureInfo.InvariantCulture) ==
                item.GetProperty("rem").GetString(), "BigInt remainder " + id);
            Require(q * b + r == a, "BigInt div-rem identity " + id);
        }

        foreach (JsonElement item in root.GetProperty("conversion_cases").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string operation = item.GetProperty("operation").GetString()!;
            BigInteger value = BigInteger.Parse(item.GetProperty("operand").GetString()!,
                CultureInfo.InvariantCulture);
            if (operation == "to_i64_checked")
            {
                bool overflow = value < long.MinValue || value > long.MaxValue;
                Require(overflow == item.TryGetProperty("error", out _),
                    "BigInt i64 conversion " + id);
            }
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

    /// <summary>
    /// Independently classify exact binary64 representations with BigInteger,
    /// rather than relying on a lossy double-to-long round trip as the oracle.
    /// This is reference-helper evidence, not a guest compiler/Native AOT run.
    /// </summary>
    private static void CheckExactFloatOracleBoundaries()
    {
        long[] anchors =
        {
            long.MinValue, long.MaxValue, 0L, -1L, 1L,
            -(1L << 53), 1L << 53, -(1L << 62), 1L << 62
        };
        foreach (long anchor in anchors)
        {
            for (int offset = -4; offset <= 4; offset++)
            {
                BigInteger candidate = new BigInteger(anchor) + offset;
                if (candidate < long.MinValue || candidate > long.MaxValue)
                    continue;
                CheckExactFloatOracle((long)candidate);
            }
        }

        var random = new Random(0x0F1A);
        Span<byte> bytes = stackalloc byte[8];
        for (int i = 0; i < 10_000; i++)
        {
            random.NextBytes(bytes);
            CheckExactFloatOracle(BinaryPrimitives.ReadInt64LittleEndian(bytes));
        }
    }

    private static void CheckExactFloatOracle(long value)
    {
        double reference = (double)value;
        // BigInteger(Double) records the integer represented by the actual
        // binary64 value, including 2^63 (outside signed64's upper bound).
        bool exactlyRepresented = new BigInteger(reference) == new BigInteger(value);
        if (!exactlyRepresented)
        {
            Expect<OverflowException>(
                () => _ = StrictSigned64Arithmetic.ToFloatExact(value),
                "inexact binary64 oracle: " + value.ToString(CultureInfo.InvariantCulture));
            return;
        }

        ulong expectedBits = BitConverter.DoubleToUInt64Bits(reference);
        ulong actualBits = BitConverter.DoubleToUInt64Bits(
            StrictSigned64Arithmetic.ToFloatExact(value));
        Require(actualBits == expectedBits,
            "exact binary64 oracle bits: " + value.ToString(CultureInfo.InvariantCulture));
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
