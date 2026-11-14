namespace Ores.Net.Runtime;

/// <summary>
/// NativeAOT-safe static source identity metadata. This is NOT CLR JIT, .atto
/// guest probe emission, or a replacement for runtime W3C trace/span context.
/// </summary>
internal static class NativeSourceIdentityV1
{
    internal enum Kind { Routine, Site }

    internal readonly record struct Id(Kind SourceKind, ulong High, ulong Low);
    internal readonly record struct Site(Id RoutineId, Id SiteId, int DescriptorIndex);

    private static int Hex(char c) =>
        c is >= '0' and <= '9' ? c - '0' :
        c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;

    internal static Id Parse(string? value, Kind kind)
    {
        string prefix = kind == Kind.Routine ? "rid_" : "sid_";
        if (value is null || value.Length != 36 ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException("invalid source identity kind or width");

        ulong high = 0;
        ulong low = 0;
        for (int i = 0; i < 32; ++i)
        {
            int digit = Hex(value[i + 4]);
            if (digit < 0) throw new FormatException("source identity must be lowercase hex");
            if (i < 16) high = unchecked((high << 4) | (uint)digit);
            else low = unchecked((low << 4) | (uint)digit);
        }
        if (high == 0 && low == 0)
            throw new FormatException("all-zero source identity is not permitted");
        return new Id(kind, high, low);
    }

    /// <summary>Cold path: validates and copies an immutable fixed-size table.
    /// No reflection, runtime CIL emission, dynamic assembly or external compiler.</summary>
    internal static IReadOnlyList<Site> FreezeValidated(IReadOnlyList<Site> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        if (sites.Count > 4096)
            throw new ArgumentOutOfRangeException(nameof(sites), "source descriptor budget exceeded");
        var siteIds = new HashSet<Id>();
        var indices = new HashSet<int>();
        var frozen = new Site[sites.Count];
        for (int i = 0; i < sites.Count; ++i)
        {
            var site = sites[i];
            if (site.RoutineId.SourceKind != Kind.Routine ||
                site.SiteId.SourceKind != Kind.Site ||
                site.RoutineId.High == 0 && site.RoutineId.Low == 0 ||
                site.SiteId.High == 0 && site.SiteId.Low == 0 ||
                site.DescriptorIndex < 0 ||
                !siteIds.Add(site.SiteId) || !indices.Add(site.DescriptorIndex))
                throw new ArgumentException("invalid or duplicate static source descriptor", nameof(sites));
            frozen[i] = site;
        }
        return Array.AsReadOnly(frozen);
    }
}
