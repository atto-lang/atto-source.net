using Ores.Net.Runtime;

internal static class Complex128FiniteTests
{
    public static void Run()
    {
        AssertBits(
            Complex128Math.Multiply(
                new Complex128(1.3e-162, 1.3e-162),
                new Complex128(1.3e-162, 1.3e-162)),
            0.0, 5e-324,
            "minimum-subnormal multiplication");

        AssertBits(
            Complex128Math.Multiply(
                new Complex128(-2.876527695159415e173, 1.1062282252099771e52),
                new Complex128(37053981341.46522, 3.503535910293817e134)),
            -3.8863689923582155e186,
            -1.0078018076945717e308,
            "once-rounded separated cross terms");

        AssertBits(
            Complex128Math.Divide(
                new Complex128(-3.8267306126191597e229, -2.653330234432702e-108),
                new Complex128(4.996866008987324e199, 5.230549533052834e-126)),
            -7.658261409724479e29,
            8.016407798119527e-296,
            "representable tiny division cross component");

        AssertBits(
            Complex128Math.Divide(
                new Complex128(-1.9297622926115045e138, 1.5512392579051227e150),
                new Complex128(-1.4976784258940757e292, -1.8366476025309775e288)),
            -1.270186418667072e-146,
            -1.0357625560708896e-142,
            "balanced one-ULP division");

        AssertBits(
            Complex128Math.Divide(
                new Complex128(0.9999999947592926, 1.0000000008845844),
                new Complex128(0.9999999973991033, 1.0000000035243972)),
            0.9999999973601883, -1.0466270614337854e-15,
            "nonzero near-cancellation division");

        AssertBits(
            Complex128Math.Divide(
                new Complex128(1e300, 1e-20),
                new Complex128(0.0, 1.0)),
            1e-20, -1e300,
            "small component survives extreme scale separation");

        AssertBits(
            Complex128Math.Divide(
                new Complex128(0.0, 0.0),
                new Complex128(0.0, 5e-324)),
            0.0, 0.0,
            "zero numerator over minimum subnormal divisor");

        ExpectArithmetic(
            () => Complex128Math.Multiply(
                new Complex128(double.PositiveInfinity, 0.0),
                new Complex128(1.0, 0.0)),
            "exceptional multiply must remain fail-closed");
        ExpectArithmetic(
            () => Complex128Math.Divide(
                new Complex128(1.0, 0.0),
                new Complex128(0.0, 0.0)),
            "zero divisor must remain fail-closed");
    }

    private static void AssertBits(
        Complex128 actual, double real, double imaginary, string label)
    {
        if (BitConverter.DoubleToInt64Bits(actual.Real) !=
                BitConverter.DoubleToInt64Bits(real) ||
            BitConverter.DoubleToInt64Bits(actual.Imaginary) !=
                BitConverter.DoubleToInt64Bits(imaginary))
            throw new Exception("FAILED complex128 bits: " + label);
    }

    private static void ExpectArithmetic(Action action, string label)
    {
        try { action(); }
        catch (ArithmeticException) { return; }
        throw new Exception("FAILED: " + label);
    }
}
