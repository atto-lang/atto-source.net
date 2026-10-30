namespace Ores.Net.Runtime;

/// <summary>
/// Independent signed-64 Oreslang arithmetic semantics for the .NET backend.
/// The scalar compiler admits only Integer.rem through verified SSA and a
/// static CIL call. Mod, Pow, BitXor and ToFloatExact remain host reference
/// helpers until each receives typed guest source and Native AOT execution.
/// CLR exceptions are not yet guest Error values; do not claim error parity.
/// </summary>
public static class StrictSigned64Arithmetic
{
    public static long Neg(long value)
    {
        if (value == long.MinValue)
            throw new OverflowException("integer negation overflow");
        return -value;
    }

    public static long Abs(long value)
    {
        if (value == long.MinValue)
            throw new OverflowException("integer absolute-value overflow");
        return value < 0 ? -value : value;
    }

    public static long Rem(long dividend, long divisor)
    {
        if (divisor == 0)
            throw new DivideByZeroException("integer remainder by zero");
        // CLI 'rem' for MIN/-1 may trap although mathematical remainder is 0.
        if (dividend == long.MinValue && divisor == -1)
            return 0;
        return dividend % divisor;
    }

    public static long Mod(long dividend, long divisor)
    {
        long remainder = Rem(dividend, divisor);
        if (remainder >= 0)
            return remainder;
        // In particular, Abs(long.MinValue) is never computed.
        return divisor > 0
            ? checked(remainder + divisor)
            : checked(remainder - divisor);
    }

    public static long Pow(long value, long exponent)
    {
        if (exponent < 0)
            throw new ArgumentOutOfRangeException(nameof(exponent),
                "integer exponent must be nonnegative");
        long result = 1;
        while (exponent != 0)
        {
            if ((exponent & 1L) != 0)
                result = checked(result * value);
            exponent = (long)((ulong)exponent >> 1);
            if (exponent != 0)
                value = checked(value * value);
        }
        return result;
    }

    /// <summary>
    /// Explicit, lossless conversion to IEEE754 binary64. Unlike unchecked
    /// long casts, this rejects values rounding outside the i64 upper bound.
    /// Oreslang source `as` is a type assertion, not a numeric conversion.
    /// </summary>
    public static double ToFloatExact(long value)
    {
        double converted = (double)value;
        if (converted >= 9223372036854775808.0d
            || (long)converted != value)
            throw new OverflowException("inexact signed64 to binary64 conversion");
        return converted;
    }

    public static long BitXor(long left, long right) => left ^ right;
}
