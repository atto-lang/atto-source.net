using System.Numerics;

namespace Ores.Net.Compiler;

// Range evidence describes normal outcomes. It does not remove arithmetic checks.
internal readonly record struct VerifiedI64Range
{
    private readonly long low, high;
    private readonly int exclusions;
    private static int Bit(long n) => n == 0 ? 1 : n == -1 ? 2 : 0;

    public VerifiedI64Range(long low, long high, int exclusions = 0)
    {
        while (low <= high && (exclusions & Bit(low)) != 0) low++;
        while (low <= high && (exclusions & Bit(high)) != 0) high--;
        if (low > high) throw Empty();
        if (low > 0 || high < 0) exclusions &= ~1;
        if (low > -1 || high < -1) exclusions &= ~2;
        this.low = low;
        this.high = high;
        this.exclusions = exclusions;
    }
    internal bool ContainsValue(long n) => low <= n && n <= high && (exclusions & Bit(n)) == 0;
    internal VerifiedI64Range Excluding(long n)
    {
        if (!ContainsValue(n)) return this;
        if (low == high) throw Empty();
        if (low == n) return new(low+1, high, exclusions);
        if (high == n) return new(low, high-1, exclusions);
        return new(low, high, exclusions | Bit(n));
    }
    internal VerifiedI64Range Join(VerifiedI64Range other) => new(
        Math.Min(low, other.low), Math.Max(high, other.high),
        (!ContainsValue(0) && !other.ContainsValue(0) ? 1 : 0)
        | (!ContainsValue(-1) && !other.ContainsValue(-1) ? 2 : 0));

    internal static VerifiedI64Range Full => new(long.MinValue, long.MaxValue);
    internal static VerifiedI64Range Point(long value) => new(value, value);

    internal bool Includes(VerifiedI64Range actual) => low <= actual.low && high >= actual.high
        && (!actual.ContainsValue(0) || ContainsValue(0)) && (!actual.ContainsValue(-1) || ContainsValue(-1));

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
        "eq" => new(Math.Max(low, value), Math.Min(high, value), exclusions),
        "<=" => new(low, Math.Min(high, value), exclusions),
        ">=" => new(Math.Max(low, value), high, exclusions),
        "<" when value != long.MinValue => new(low, Math.Min(high, value - 1), exclusions),
        ">" when value != long.MaxValue => new(Math.Max(low, value + 1), high, exclusions),
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
