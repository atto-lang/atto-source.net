using System.Numerics;

namespace Ores.Net.Runtime;

/// <summary>
/// Binary64 complex scalar used by the .NET runtime reference kernel.
///
/// Finite multiplication/division decode each IEEE-754 input to its exact
/// dyadic value, evaluate exactly with BigInteger and round each output
/// component once using round-to-nearest, ties-to-even. Exceptional
/// NaN/Infinity semantics remain fail-closed until the versioned IEEE-special
/// contract is wired into this backend.
/// </summary>
public readonly record struct Complex128(double Real, double Imaginary);

public static class Complex128Math
{
    private static readonly BigInteger TwoPow52 = BigInteger.One << 52;
    private static readonly BigInteger TwoPow53 = BigInteger.One << 53;

    public static Complex128 Add(Complex128 left, Complex128 right) =>
        new(left.Real + right.Real, left.Imaginary + right.Imaginary);

    public static Complex128 Subtract(Complex128 left, Complex128 right) =>
        new(left.Real - right.Real, left.Imaginary - right.Imaginary);

    public static Complex128 Conjugate(Complex128 value) =>
        new(value.Real, -value.Imaginary);

    public static Complex128 Multiply(Complex128 left, Complex128 right)
    {
        RequireFinite(left, "complex128 multiply");
        RequireFinite(right, "complex128 multiply");

        double primitiveReal = left.Real * right.Real -
                               left.Imaginary * right.Imaginary;
        double primitiveImaginary = left.Real * right.Imaginary +
                                    left.Imaginary * right.Real;

        var a = FromFiniteDouble(left.Real);
        var b = FromFiniteDouble(left.Imaginary);
        var c = FromFiniteDouble(right.Real);
        var d = FromFiniteDouble(right.Imaginary);

        var exactReal = AddDyadic(MultiplyDyadic(a, c),
                                  Negate(MultiplyDyadic(b, d)));
        var exactImaginary = AddDyadic(MultiplyDyadic(a, d),
                                       MultiplyDyadic(b, c));
        return new Complex128(
            RoundDyadic(exactReal, NegativeZeroHint(primitiveReal)),
            RoundDyadic(exactImaginary, NegativeZeroHint(primitiveImaginary)));
    }

    public static Complex128 Divide(Complex128 numerator, Complex128 denominator)
    {
        RequireFinite(numerator, "complex128 divide");
        RequireFinite(denominator, "complex128 divide");
        if (denominator.Real == 0.0 && denominator.Imaginary == 0.0)
            throw new ArithmeticException(
                "complex128 finite divide requires a nonzero divisor");

        var a = FromFiniteDouble(numerator.Real);
        var b = FromFiniteDouble(numerator.Imaginary);
        var c = FromFiniteDouble(denominator.Real);
        var d = FromFiniteDouble(denominator.Imaginary);

        var exactDenominator = AddDyadic(
            MultiplyDyadic(c, c), MultiplyDyadic(d, d));
        var realNumerator = AddDyadic(
            MultiplyDyadic(a, c), MultiplyDyadic(b, d));
        var imaginaryNumerator = AddDyadic(
            MultiplyDyadic(b, c), Negate(MultiplyDyadic(a, d)));

        double primitiveReal =
            numerator.Real * denominator.Real +
            numerator.Imaginary * denominator.Imaginary;
        double primitiveImaginary =
            numerator.Imaginary * denominator.Real -
            numerator.Real * denominator.Imaginary;

        return new Complex128(
            RoundRatio(realNumerator, exactDenominator,
                NegativeZeroHint(primitiveReal)),
            RoundRatio(imaginaryNumerator, exactDenominator,
                NegativeZeroHint(primitiveImaginary)));
    }

    private static void RequireFinite(Complex128 value, string operation)
    {
        if (!double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary))
            throw new ArithmeticException(
                operation + " exceptional values are not admitted until the " +
                "IEEE-special backend contract is implemented");
    }

    private readonly record struct Dyadic(BigInteger Significand, int Exponent2);

    private static Dyadic FromFiniteDouble(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value),
                "value must be finite");

        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        bool negative = (bits & 0x8000_0000_0000_0000UL) != 0;
        int exponentBits = (int)((bits >> 52) & 0x7ffUL);
        ulong fraction = bits & 0x000f_ffff_ffff_ffffUL;
        if (exponentBits == 0 && fraction == 0)
            return new Dyadic(BigInteger.Zero, 0);

        BigInteger significand;
        int exponent2;
        if (exponentBits == 0)
        {
            significand = new BigInteger(fraction);
            exponent2 = -1074;
        }
        else
        {
            significand = new BigInteger(fraction | (1UL << 52));
            exponent2 = checked(exponentBits - 1023 - 52);
        }
        if (negative) significand = -significand;
        return Normalize(new Dyadic(significand, exponent2));
    }

    private static Dyadic MultiplyDyadic(Dyadic left, Dyadic right)
    {
        if (left.Significand.IsZero || right.Significand.IsZero)
            return new Dyadic(BigInteger.Zero, 0);
        return Normalize(new Dyadic(
            left.Significand * right.Significand,
            checked(left.Exponent2 + right.Exponent2)));
    }

    private static Dyadic AddDyadic(Dyadic left, Dyadic right)
    {
        if (left.Significand.IsZero) return right;
        if (right.Significand.IsZero) return left;

        int common = Math.Min(left.Exponent2, right.Exponent2);
        BigInteger leftSig =
            left.Significand << checked(left.Exponent2 - common);
        BigInteger rightSig =
            right.Significand << checked(right.Exponent2 - common);
        return Normalize(new Dyadic(leftSig + rightSig, common));
    }

    private static Dyadic Negate(Dyadic value) =>
        new(-value.Significand, value.Exponent2);

    private static Dyadic Normalize(Dyadic value)
    {
        if (value.Significand.IsZero)
            return new Dyadic(BigInteger.Zero, 0);

        BigInteger magnitude = BigInteger.Abs(value.Significand);
        int trailing = 0;
        while (magnitude.IsEven)
        {
            magnitude >>= 1;
            trailing++;
        }
        if (trailing == 0) return value;
        return new Dyadic(
            value.Significand >> trailing,
            checked(value.Exponent2 + trailing));
    }

    private static double RoundDyadic(Dyadic value, bool negativeZero)
    {
        if (value.Significand.IsZero)
            return BitsToDouble(negativeZero
                ? 0x8000_0000_0000_0000UL
                : 0UL);

        return RoundPositiveRational(
            BigInteger.Abs(value.Significand),
            BigInteger.One,
            value.Exponent2,
            value.Significand.Sign < 0);
    }

    private static double RoundRatio(
        Dyadic numerator, Dyadic denominator, bool negativeZero)
    {
        if (denominator.Significand.Sign <= 0)
            throw new ArgumentOutOfRangeException(nameof(denominator),
                "denominator must be positive");
        if (numerator.Significand.IsZero)
            return BitsToDouble(negativeZero
                ? 0x8000_0000_0000_0000UL
                : 0UL);

        return RoundPositiveRational(
            BigInteger.Abs(numerator.Significand),
            denominator.Significand,
            checked(numerator.Exponent2 - denominator.Exponent2),
            numerator.Significand.Sign < 0);
    }

    private static double RoundPositiveRational(
        BigInteger numerator,
        BigInteger denominator,
        int exponent2,
        bool negative)
    {
        if (numerator.Sign <= 0 || denominator.Sign <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator),
                "ratio must be positive");

        int binaryExponent = checked(
            FloorLog2Ratio(numerator, denominator) + exponent2);
        ulong signBit = negative ? 0x8000_0000_0000_0000UL : 0UL;

        if (binaryExponent < -1022)
        {
            BigInteger q = RoundQuotient(
                numerator, denominator, checked(exponent2 + 1074));
            if (q.IsZero) return BitsToDouble(signBit);
            if (q >= TwoPow52)
                return BitsToDouble(signBit | (1UL << 52));
            return BitsToDouble(signBit | checked((ulong)q));
        }

        int shift = checked(exponent2 - (binaryExponent - 52));
        BigInteger normal = RoundQuotient(numerator, denominator, shift);
        if (normal >= TwoPow53)
        {
            normal >>= 1;
            binaryExponent = checked(binaryExponent + 1);
        }
        if (binaryExponent > 1023)
            return negative ? double.NegativeInfinity : double.PositiveInfinity;

        ulong exponentField =
            checked((ulong)(binaryExponent + 1023)) << 52;
        ulong fraction =
            checked((ulong)(normal - TwoPow52));
        return BitsToDouble(signBit | exponentField | fraction);
    }

    private static int FloorLog2Ratio(
        BigInteger numerator, BigInteger denominator)
    {
        int candidate = checked(
            (int)numerator.GetBitLength() -
            (int)denominator.GetBitLength());
        bool atLeast = candidate >= 0
            ? numerator >= (denominator << candidate)
            : (numerator << -candidate) >= denominator;
        return atLeast ? candidate : checked(candidate - 1);
    }

    private static BigInteger RoundQuotient(
        BigInteger numerator, BigInteger denominator, int binaryShift)
    {
        BigInteger scaledNumerator = numerator;
        BigInteger scaledDenominator = denominator;
        if (binaryShift >= 0)
            scaledNumerator <<= binaryShift;
        else
            scaledDenominator <<= -binaryShift;

        BigInteger quotient =
            BigInteger.DivRem(scaledNumerator, scaledDenominator,
                out BigInteger remainder);
        int halfCompare =
            (remainder << 1).CompareTo(scaledDenominator);
        if (halfCompare > 0 ||
            (halfCompare == 0 && !quotient.IsEven))
            quotient += BigInteger.One;
        return quotient;
    }

    private static bool NegativeZeroHint(double value) =>
        value == 0.0 && BitConverter.DoubleToInt64Bits(value) < 0;

    private static double BitsToDouble(ulong bits) =>
        BitConverter.Int64BitsToDouble(unchecked((long)bits));
}
