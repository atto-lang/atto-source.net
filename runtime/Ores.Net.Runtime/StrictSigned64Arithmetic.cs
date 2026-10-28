namespace Ores.Net.Runtime;

/// <summary>
/// Reference signed-64 Oreslang named arithmetic semantics for the .NET
/// backend. Does not bind guest syntax: the parser and Native AOT emitter
/// must enable each operation only after checked type/effect admission.
/// </summary>
public static class StrictSigned64Arithmetic
{
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

    public static long BitXor(long left, long right) => left ^ right;
}
