using System.Numerics;

namespace Ores.Net.Compiler;

// Range evidence describes normal outcomes. It does not remove arithmetic checks.
internal readonly record struct VerifiedI64Range
{
    private readonly long low, high;
    private readonly byte holes;

    public VerifiedI64Range(long low, long high) : this(low,high,0) { }

    private VerifiedI64Range(long low, long high, byte holes)
    {
        if (low > high) throw Empty();
        while ((holes & Bit(low)) != 0) low++;
        while ((holes & Bit(high)) != 0) high--;
        if (low > high) throw Empty();
        this.low = low;
        this.high = high;
        this.holes = holes;
    }

    internal static VerifiedI64Range Full => new(long.MinValue, long.MaxValue);
    internal static VerifiedI64Range Point(long value) => new(value, value);

    private static byte Bit(long n) => n == 0 ? (byte)1 : n == -1 ? (byte)2 : (byte)0;
    internal bool Has(long n) => n >= low && n <= high && (holes & Bit(n)) == 0;
    internal bool Includes(VerifiedI64Range actual) => low <= actual.low && high >= actual.high
        && (!actual.Has(0) || Has(0)) && (!actual.Has(-1) || Has(-1));
    internal VerifiedI64Range Join(VerifiedI64Range b) => new(Math.Min(low,b.low),Math.Max(high,b.high),
        (byte)((!Has(0)&&!b.Has(0)?1:0)|(!Has(-1)&&!b.Has(-1)?2:0)));
    internal VerifiedI64Range Restrict(string op,long n)
    {
        // Contradictory branches remain conservatively checked, never
        // certified unreachable by an empty interval.
        try {
            if(op=="!=") {
                if(!Has(n)) return this;
                if(n==low) return n==long.MaxValue?this:new(low+1,high,holes);
                if(n==high) return n==long.MinValue?this:new(low,high-1,holes);
                return new(low,high,(byte)(holes|Bit(n)));
            }
            var r=Constrain(op=="=="?"eq":op,n);
            return new(r.low,r.high,holes);
        } catch(FormatException) { return this; }
    }

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
        "!=" when value is 0 or -1 => !Has(value) ? this : low == high ? throw Empty() : new(low, high, (byte)(holes | Bit(value))),
        "!=" => throw new FormatException("E_REFINEMENT_CONTRACT: unsupported exclusion"),
        "eq" => new(Math.Max(low, value), Math.Min(high, value), holes),
        "<=" => new(low, Math.Min(high, value), holes),
        ">=" => new(Math.Max(low, value), high, holes),
        "<" when value != long.MinValue => new(low, Math.Min(high, value - 1), holes),
        ">" when value != long.MaxValue => new(Math.Max(low, value + 1), high, holes),
        "<" or ">" => throw Empty(),
        _ => throw new FormatException("E_REFINEMENT_PREDICATE: expected an interval comparison"),
    };

    // Universal safety, not clipped normal-result facts.
    internal bool ProvesArithmetic(string op, VerifiedI64Range right)
    {
        static bool Fits(BigInteger a, BigInteger b) => a >= long.MinValue && b <= long.MaxValue;
        switch (op)
        {
            case "+": return Fits((BigInteger)low + right.low, (BigInteger)high + right.high);
            case "-": return Fits((BigInteger)low - right.high, (BigInteger)high - right.low);
            case "*":
                BigInteger a = (BigInteger)low * right.low, b = (BigInteger)low * right.high,
                    c = (BigInteger)high * right.low, d = (BigInteger)high * right.high;
                return Fits(BigInteger.Min(BigInteger.Min(a,b),BigInteger.Min(c,d)),
                    BigInteger.Max(BigInteger.Max(a,b),BigInteger.Max(c,d)));
            case "/": return !right.Has(0) && !(Has(long.MinValue) && right.Has(-1));
            default: return false;
        }
    }
    internal bool ProvesNegation => low != long.MinValue;

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
