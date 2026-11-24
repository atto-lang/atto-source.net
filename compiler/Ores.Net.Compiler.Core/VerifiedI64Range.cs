using System.Numerics;

namespace Ores.Net.Compiler;

// Range evidence describes normal outcomes. It does not remove arithmetic checks.
internal readonly record struct VerifiedI64Range
{
    private readonly long low, high;

    public VerifiedI64Range(long low, long high)
    {
        if (low > high) throw Empty();
        this.low = low;
        this.high = high;
    }

    internal static VerifiedI64Range Full => new(long.MinValue, long.MaxValue);
    internal static VerifiedI64Range Point(long value) => new(value, value);

    internal bool Includes(VerifiedI64Range actual) => low <= actual.low && high >= actual.high;

    internal long Minimum => low;
    internal long Maximum => high;

    internal bool Nonnegative => low >= 0;
    internal bool Positive => low > 0;
    internal bool Nonpositive => high <= 0;
    internal bool AtMost(VerifiedI64Range right) => high <= right.low;

    internal static VerifiedI64Range SignedI32 => new(int.MinValue, int.MaxValue);

    internal VerifiedI64Range OnNormalResultWithin(VerifiedI64Range carrier)
    {
        long first = Math.Max(low, carrier.low);
        long last = Math.Min(high, carrier.high);
        return first <= last ? new(first, last) : carrier;
    }

    internal VerifiedI64Range Constrain(string op, long value) => op switch
    {
        "eq" => new(Math.Max(low, value), Math.Min(high, value)),
        "<=" => new(low, Math.Min(high, value)),
        ">=" => new(Math.Max(low, value), high),
        "<" when value != long.MinValue => new(low, Math.Min(high, value - 1)),
        ">" when value != long.MaxValue => new(Math.Max(low, value + 1), high),
        "<" or ">" => throw Empty(),
        _ => throw new FormatException("E_REFINEMENT_PREDICATE: expected an interval comparison"),
    };

    internal VerifiedI64Range Add(VerifiedI64Range right) =>
        Clip((BigInteger)low + right.low, (BigInteger)high + right.high);

    internal VerifiedI64Range Subtract(VerifiedI64Range right) =>
        Clip((BigInteger)low - right.high, (BigInteger)high - right.low);

    internal VerifiedI64Range Multiply(VerifiedI64Range right)
    {
        BigInteger a = (BigInteger)low * right.low, b = (BigInteger)low * right.high,
            c = (BigInteger)high * right.low, d = (BigInteger)high * right.high;
        return Clip(BigInteger.Min(BigInteger.Min(a, b), BigInteger.Min(c, d)),
            BigInteger.Max(BigInteger.Max(a, b), BigInteger.Max(c, d)));
    }

    internal VerifiedI64Range Divide(VerifiedI64Range right)
    {
        if ((right.low <= 0 && right.high >= 0)
            || (low == long.MinValue && right.low <= -1 && right.high >= -1))
            return Full;
        long a = low / right.low, b = low / right.high, c = high / right.low, d = high / right.high;
        return new(Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Max(Math.Max(a, b), Math.Max(c, d)));
    }

    private static VerifiedI64Range Clip(BigInteger first, BigInteger last)
    {
        first = BigInteger.Max(first, long.MinValue);
        last = BigInteger.Min(last, long.MaxValue);
        return first <= last ? new VerifiedI64Range((long)first, (long)last) : Full;
    }

    private static FormatException Empty() => new("E_REFINEMENT_EMPTY: contradictory interval");
}
