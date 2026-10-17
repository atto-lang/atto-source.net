using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Ores.Net.Runtime;

/// <summary>
/// Immutable host-owned capability ceiling plus scoped allow/deny substrate.
/// NOT a syscall interceptor or evidence of Oreslang guest I/O implementation.
/// The actual host I/O adapter must call Allows before every operation.
/// </summary>
public sealed class ScopedPermissionPolicy
{
    public enum Kind { Read, Write, Net, Env, Run, Sys, Ffi, Import }

    public sealed record Rule(Kind Permission, string Scope, bool Denied = false);

    private readonly bool[] ceiling = new bool[8];
    private readonly List<string>[] allow = Enumerable.Range(0, 8)
        .Select(_ => new List<string>()).ToArray();
    private readonly List<string>[] deny = Enumerable.Range(0, 8)
        .Select(_ => new List<string>()).ToArray();
    private readonly ScopedPermissionPolicy? runtimeGrant;
    private readonly ScopedPermissionPolicy? buildCeiling;

    private ScopedPermissionPolicy() { }

    private ScopedPermissionPolicy(
        ScopedPermissionPolicy runtimeGrant,
        ScopedPermissionPolicy buildCeiling)
    {
        this.runtimeGrant = runtimeGrant ?? throw new ArgumentNullException(nameof(runtimeGrant));
        this.buildCeiling = buildCeiling ?? throw new ArgumentNullException(nameof(buildCeiling));
    }

    /// <summary>
    /// Return a policy whose effective authority is the intersection of this
    /// runtime grant and an immutable build/host ceiling. Chaining only narrows.
    /// </summary>
    public ScopedPermissionPolicy BoundedBy(ScopedPermissionPolicy ceilingPolicy) =>
        new(this, ceilingPolicy);

    public static ScopedPermissionPolicy DefaultDeny() => new();

    public static ScopedPermissionPolicy FromHost(
        IEnumerable<Kind> capabilities, IEnumerable<Rule> scopes)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(scopes);
        var policy = new ScopedPermissionPolicy();
        var capabilityCount = 0;
        foreach (var capability in capabilities)
        {
            if (++capabilityCount > 8)
                throw new ArgumentException("oversized capability list", nameof(capabilities));
            policy.ceiling[Index(capability)] = true;
        }
        var ruleCount = 0;
        foreach (var rule in scopes)
        {
            if (++ruleCount > 512)
                throw new ArgumentException("oversized permission policy", nameof(scopes));
            if (rule is null || !ValidScope(rule.Permission, rule.Scope))
                throw new ArgumentException("invalid permission scope", nameof(scopes));
            var i = Index(rule.Permission);
            (rule.Denied ? policy.deny[i] : policy.allow[i]).Add(rule.Scope);
        }
        return policy;
    }

    private static int Index(Kind permission)
    {
        var index = (int)permission;
        if (index is < 0 or >= 8)
            throw new ArgumentOutOfRangeException(nameof(permission));
        return index;
    }

    private static bool HasControl(string text) =>
        text.Any(ch => ch < 32 || ch == 127);

    private static bool PathValid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path[0] != '/' || HasControl(path) ||
            (path.Length > 1 && path.EndsWith('/')))
            return false;
        if (path == "/") return true; // Explicit root scope, matching native C++ policy.
        return path.Split('/').Skip(1).All(part =>
            part.Length > 0 && part is not ("." or ".."));
    }

    private static bool TokenValid(string? token, bool rule)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 1024 || HasControl(token)) return false;
        var star = token.IndexOf('*');
        return star < 0 || (rule && (token == "*" ||
            (star == token.Length - 1 && token.LastIndexOf('*') == star)));
    }

    private readonly record struct Endpoint(string Host, int? Port, bool Subdomain);

    // Numeric IPv4 and bracketed IPv6 addresses use canonical identity.
    // IPv4-mapped/embedded and zone-scoped IPv6 forms fail closed.
    // DNS resolution and selected-IP checks belong to a trusted socket adapter.
    private static bool TryEndpoint(string? raw, bool rule, out Endpoint value)
    {
        value = default;
        if (string.IsNullOrEmpty(raw) || raw.Length > 320 || HasControl(raw) ||
            raw.Any(ch => char.IsWhiteSpace(ch) || ch is '/' or '@' or '\\'))
            return false;
        var text = raw.ToLowerInvariant();
        if (rule && text == "*")
        {
            value = new Endpoint("*", null, false);
            return true;
        }
        if (text.StartsWith('['))
        {
            // Bracketed numeric IPv6 only. Never resolve a hostname here.
            // Reject zone identifiers and IPv4-mapped identities until their
            // authority is separately specified by the host.
            var close = text.IndexOf(']');
            if (close <= 1) return false;
            int? ipv6Port = null;
            if (close + 1 < text.Length)
            {
                if (text[close + 1] != ':' || close + 2 == text.Length)
                    return false;
                var digits = text[(close + 2)..];
                if (digits.Length is < 1 or > 5 ||
                    digits.Any(ch => ch is < '0' or > '9') ||
                    !int.TryParse(digits, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 1 or > 65535)
                    return false;
                ipv6Port = parsed;
            }
            var literal = text[1..close];
            if (literal.Contains('%') || literal.Contains('.') ||
                !IPAddress.TryParse(literal, out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetworkV6 ||
                ip.IsIPv4MappedToIPv6)
                return false;
            var normalized = "[" +
                Convert.ToHexString(ip.GetAddressBytes()).ToLowerInvariant() + "]";
            value = new Endpoint(normalized, ipv6Port, false);
            return true;
        }
        int? port = null;
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            if (text.LastIndexOf(':') != colon || colon == text.Length - 1)
                return false;
            var digits = text[(colon + 1)..];
            if (digits.Length is < 1 or > 5 || digits.Any(c => c is < '0' or > '9') ||
                !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var parsed) || parsed is < 1 or > 65535)
                return false;
            port = parsed;
            text = text[..colon];
        }
        if (text.EndsWith('.')) text = text[..^1];
        var subdomain = rule && text.StartsWith("*.", StringComparison.Ordinal);
        if (subdomain) text = text[2..];
        if (text.Length is < 1 or > 253 || text.StartsWith('.') || text.EndsWith('.'))
            return false;
        var labels = text.Split('.');
        if (labels.Any(label => label.Length is < 1 or > 63 ||
            label.StartsWith('-') || label.EndsWith('-') ||
            label.Any(c => !((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))))
            return false;
        // Mixed hex/decimal labels can be interpreted as numeric IPv4 by
        // libc-style resolvers; deny aliases such as 127.0x0.0.1 and
        // 1.2.3.0x01, not only 0x-prefixed first labels.
        var numericAddressAlias = labels.All(label =>
            label.All(c => c is >= '0' and <= '9') ||
            (label.Length > 2 && label.StartsWith("0x", StringComparison.Ordinal)
                && label[2..].All(c =>
                    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))));
        if (numericAddressAlias &&
            !labels.All(label => label.All(c => c is >= '0' and <= '9')))
            return false;
        if (labels.All(label => label.All(c => c is >= '0' and <= '9')))
        {
            // Numeric address aliases must not become alternative DNS names.
            if (labels.Length != 4 || labels.Any(label => label.Length > 3 ||
                (label.Length > 1 && label[0] == '0') ||
                !byte.TryParse(label, NumberStyles.None,
                    CultureInfo.InvariantCulture, out _)))
                return false;
        }
        value = new Endpoint(text, port, subdomain);
        return true;
    }

    private static bool HostMatches(Endpoint target, Endpoint rule)
    {
        if (!rule.Subdomain) return target.Host == rule.Host;
        var suffix = "." + rule.Host;
        return target.Host.Length > suffix.Length &&
            target.Host.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static bool Matches(Kind kind, string resource, string scope)
    {
        if (scope == "*") return true;
        switch (kind)
        {
            case Kind.Read: case Kind.Write: case Kind.Ffi: case Kind.Import:
                return resource == scope ||
                    (scope == "/" ? resource.Length > 1 :
                        resource.StartsWith(scope + "/", StringComparison.Ordinal));
            case Kind.Net:
                return TryEndpoint(resource, false, out var target) &&
                    TryEndpoint(scope, true, out var rule) &&
                    (rule.Port is null || target.Port == rule.Port) &&
                    HostMatches(target, rule);
            case Kind.Env: case Kind.Run: case Kind.Sys:
                return scope.EndsWith('*')
                    ? resource.StartsWith(scope[..^1], StringComparison.Ordinal)
                    : resource == scope;
            default:
                return false;
        }
    }

    private static bool ValidScope(Kind kind, string? scope)
    {
        _ = Index(kind);
        if (scope == "*") return true;
        return kind switch
        {
            Kind.Read or Kind.Write or Kind.Ffi or Kind.Import => PathValid(scope),
            Kind.Net => TryEndpoint(scope, true, out _),
            Kind.Env or Kind.Run or Kind.Sys => TokenValid(scope, true),
            _ => false
        };
    }

    public bool Allows(Kind permission, string? resource)
    {
        if (runtimeGrant is not null || buildCeiling is not null)
            return runtimeGrant!.Allows(permission, resource) &&
                buildCeiling!.Allows(permission, resource);
        var index = Index(permission);
        if (!ceiling[index] || string.IsNullOrEmpty(resource) || HasControl(resource))
            return false;
        switch (permission)
        {
            case Kind.Read: case Kind.Write: case Kind.Ffi: case Kind.Import:
                if (!PathValid(resource)) return false;
                break;
            case Kind.Env: case Kind.Run: case Kind.Sys:
                if (!TokenValid(resource, false)) return false;
                break;
            case Kind.Net:
                if (!TryEndpoint(resource, false, out var target)) return false;
                if (target.Port is null)
                {
                    foreach (var blocked in deny[index])
                    {
                        if (TryEndpoint(blocked, true, out var rule) &&
                            rule.Port is not null && HostMatches(target, rule))
                            return false;
                    }
                }
                break;
        }
        if (deny[index].Any(scope => Matches(permission, resource, scope))) return false;
        return allow[index].Any(scope => Matches(permission, resource, scope));
    }

    /// <summary>
    /// The trusted socket adapter supplies the actual selected numeric IPv4
    /// endpoint, including port, immediately before connecting. Hostname
    /// permission cannot override a resolved-IP deny. Policy-only: this method
    /// does not perform DNS, pin socket addresses, or intercept guest calls.
    /// IPv6 and ambiguous numeric forms remain denied until supported safely.
    /// </summary>
    public bool AllowsNetworkConnection(string? requested, string? selectedIpv4)
    {
        if (runtimeGrant is not null || buildCeiling is not null)
            return runtimeGrant!.AllowsNetworkConnection(requested, selectedIpv4) &&
                buildCeiling!.AllowsNetworkConnection(requested, selectedIpv4);
        if (!Allows(Kind.Net, requested) ||
            !TryEndpoint(requested, false, out var target) ||
            !TryEndpoint(selectedIpv4, false, out var selected) ||
            target.Port is null || selected.Port is null ||
            target.Port != selected.Port)
            return false;

        // TryEndpoint already canonicalizes and validates all-numeric hosts;
        // disallow resolved DNS spellings, hex/abbreviated IPs, and IPv6.
        var octets = selected.Host.Split('.');
        if (octets.Length != 4 || octets.Any(label =>
            label.Length is < 1 or > 3 ||
            label.Any(c => c is < '0' or > '9') ||
            (label.Length > 1 && label[0] == '0') ||
            !byte.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return false;

        // Unspecified IPv4 is a bind address, not a remote connection
        // identity. On Linux, connecting to 0.0.0.0 can reach 127.0.0.1,
        // defeating an otherwise effective loopback-deny scope.
        if (selected.Host == "0.0.0.0" || target.Host == "0.0.0.0")
            return false;

        // A numeric requested endpoint already identifies the destination;
        // a trusted adapter must not use a different selected IP to satisfy
        // an authorization that was granted only to that literal.
        // This helper is IPv4-only; IPv6 requested literals fail closed.
        if (target.Host.StartsWith("[", StringComparison.Ordinal))
            return false;
        if (target.Host.All(c => c == '.' || (c >= '0' && c <= '9')) &&
            !string.Equals(target.Host, selected.Host, StringComparison.Ordinal))
            return false;

        return !deny[Index(Kind.Net)].Any(scope =>
            Matches(Kind.Net, selectedIpv4!, scope));
    }
}
