using System;
using System.Collections.Generic;

namespace Ores.Net.Runtime;

/// <summary>
/// Host-only, fail-closed reader for the *scoped-permission portion* of the
/// Oreslang serialized isolate-policy envelope. It cannot authenticate the
/// caller, grant OS authority, or intercept guest operations.
/// Unknown host-policy fields deliberately fail closed until implemented.
/// </summary>
public static class ScopedPermissionEnvelope
{
    private const string CapabilityPrefix = "--ores-capabilities=";
    private const string PermissionPrefix = "--ores-permission-";
    private const string ExplicitMarker = "--ores-permission-explicit=true";

    public static ScopedPermissionPolicy FromSerializedArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var seenRules = new HashSet<string>(StringComparer.Ordinal);
        var capabilities = new HashSet<ScopedPermissionPolicy.Kind>();
        var rules = new List<ScopedPermissionPolicy.Rule>();
        string? header = null;
        bool marked = false;
        int count = 0;
        foreach (string? arg in arguments)
        {
            if (++count > 528 || arg is null || arg.Length > 8192)
                throw new ArgumentException("invalid serialized permission argument", nameof(arguments));
            if (arg.StartsWith(CapabilityPrefix, StringComparison.Ordinal))
            {
                if (header is not null)
                    throw new ArgumentException("duplicate coarse capability header", nameof(arguments));
                header = arg[CapabilityPrefix.Length..];
                continue;
            }
            if (arg == ExplicitMarker)
            {
                if (marked)
                    throw new ArgumentException("duplicate explicit permission marker", nameof(arguments));
                marked = true;
                continue;
            }
            if (!arg.StartsWith(PermissionPrefix, StringComparison.Ordinal) ||
                !seenRules.Add(arg))
                throw new ArgumentException("unknown or duplicate permission field", nameof(arguments));

            string field = arg[PermissionPrefix.Length..];
            int equals = field.IndexOf('=');
            if (equals <= 0)
                throw new ArgumentException("malformed permission field", nameof(arguments));
            string name = field[..equals];
            string value = field[(equals + 1)..];
            bool deny;
            if (name.EndsWith("-allow", StringComparison.Ordinal))
            {
                deny = false;
                name = name[..^6];
            }
            else if (name.EndsWith("-deny", StringComparison.Ordinal))
            {
                deny = true;
                name = name[..^5];
            }
            else
                throw new ArgumentException("unknown permission operation", nameof(arguments));

            var kind = PermissionKind(name);
            if (rules.Count == 512)
                throw new ArgumentException("too many scoped permission rules", nameof(arguments));
            rules.Add(new ScopedPermissionPolicy.Rule(kind, value, deny));
        }

        // A partial or truncated envelope must not fall through to ambient
        // developer/host authority. Empty header is an explicit empty set.
        if (header is null || !marked)
            throw new ArgumentException("missing coarse capabilities or explicit marker", nameof(arguments));

        if (header.Length > 1024)
            throw new ArgumentException("oversized capability header", nameof(arguments));
        if (header.Length != 0)
        {
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in header.Split(','))
            {
                if (name.Length == 0 || name != name.Trim() || !seenNames.Add(name))
                    throw new ArgumentException("malformed or duplicate coarse capability", nameof(arguments));
                capabilities.Add(CoarsePermissionKind(name));
            }
        }

        // Never elevate a narrowly-scoped grant above the coarse host
        // envelope. Deny-only fields may attenuate even an absent ceiling.
        foreach (var rule in rules)
        {
            if (!rule.Denied && !capabilities.Contains(rule.Permission))
                throw new ArgumentException("scoped grant exceeds coarse host authority", nameof(arguments));
        }
        // This also checks every syntax/port/path/token scope against the
        // exact same policy substrate used by the trusted socket adapter.
        return ScopedPermissionPolicy.FromHost(capabilities, rules);
    }

    private static ScopedPermissionPolicy.Kind PermissionKind(string name) => name switch
    {
        "read" => ScopedPermissionPolicy.Kind.Read,
        "write" => ScopedPermissionPolicy.Kind.Write,
        "net" => ScopedPermissionPolicy.Kind.Net,
        "env" => ScopedPermissionPolicy.Kind.Env,
        "run" => ScopedPermissionPolicy.Kind.Run,
        "sys" => ScopedPermissionPolicy.Kind.Sys,
        "ffi" => ScopedPermissionPolicy.Kind.Ffi,
        "import" => ScopedPermissionPolicy.Kind.Import,
        _ => throw new ArgumentException("unsupported scoped permission field")
    };

    private static ScopedPermissionPolicy.Kind CoarsePermissionKind(string name) => name switch
    {
        "FILESYSTEM_READ" => ScopedPermissionPolicy.Kind.Read,
        "FILESYSTEM_WRITE" => ScopedPermissionPolicy.Kind.Write,
        "NETWORK" => ScopedPermissionPolicy.Kind.Net,
        "ENVIRONMENT" => ScopedPermissionPolicy.Kind.Env,
        "CHILD_PROCESS" => ScopedPermissionPolicy.Kind.Run,
        "PROCESS_INFO" => ScopedPermissionPolicy.Kind.Sys,
        "FFI" => ScopedPermissionPolicy.Kind.Ffi,
        "HOT_CODE_LOAD" => ScopedPermissionPolicy.Kind.Import,
        // Other Java isolate capabilities have no audited .NET scoped-I/O
        // semantics yet. Refuse them rather than silently widening rights.
        _ => throw new ArgumentException("unsupported serialized coarse capability")
    };
}
