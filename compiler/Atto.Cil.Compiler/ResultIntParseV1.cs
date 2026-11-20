// Host-only .NET 8/NativeAOT foundation for Result<int,Error>.
// This class is NOT used by the Atto CIL source compiler until typed MIR and
// validated guest runtime binding are implemented.
namespace Atto.Cil.ResultV1;

internal abstract record ParseResult;
internal sealed record Ok(long Value) : ParseResult;
internal sealed record Err(AttoError Error) : ParseResult;
internal sealed class AttoError(long tenant_id, long identity)
{
    internal long TenantId { get; } = tenant_id;
    internal long Identity { get; } = identity;
    internal string Message => "invalid integer";
}
internal sealed class TenantParser
{
    internal const int MaxParseBytes = 1024;
    private readonly long _tenantId;
    private long _nextErrorId;
    internal TenantParser(long tenantId)
    {
        if (tenantId <= 0) throw new ArgumentOutOfRangeException(nameof(tenantId), "E_TENANT_ID");
        _tenantId = tenantId;
    }
    internal bool CanObserve(AttoError error) => error.TenantId == _tenantId && error.Identity > 0;
    private ParseResult Invalid()
    {
        if (_nextErrorId == long.MaxValue)
            throw new InvalidOperationException("E_ERROR_ID_EXHAUSTED");
        return new Err(new AttoError(_tenantId, ++_nextErrorId));
    }
    internal ParseResult Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxParseBytes) return Invalid();
        var index = 0;
        var negative = false;
        if (text[0] is '-' or '+')
        {
            negative = text[0] == '-';
            index = 1;
        }
        if (index == text.Length) return Invalid();
        var limit = negative ? long.MinValue : -long.MaxValue;
        var cutoff = limit / 10;
        long value = 0;
        for (; index < text.Length; ++index)
        {
            char digitChar = text[index];
            if (digitChar is < '0' or > '9') return Invalid();
            var digit = digitChar - '0';
            if (value < cutoff) return Invalid();
            value *= 10;
            if (value < limit + digit) return Invalid();
            value -= digit;
        }
        return new Ok(negative ? value : -value);
    }
}
