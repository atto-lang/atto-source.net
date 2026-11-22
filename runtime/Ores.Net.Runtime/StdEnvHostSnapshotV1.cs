using System;
using System.Collections.Generic;

namespace Ores.Net.Runtime;

/// <summary>
/// HOST-ONLY, NativeAOT-compatible std/env admission staging. This does NOT
/// bind a guest intrinsic and does NOT grant environment access. The trusted
/// embedding host supplies an isolated data snapshot and authenticated coarse
/// ENV capability; guest code must never control those inputs.
/// No System.Environment reads, reflection, dynamic loading or OS mutation.
/// </summary>
internal sealed class StdEnvHostSnapshotV1
{
    internal readonly record struct Lookup(bool IsPresent, string? Value);

    private readonly Dictionary<string, string> _snapshot;
    private readonly string[] _allow;
    private readonly string[] _deny;
    private readonly bool _coarseEnvironmentCapability;
    private readonly object _gate = new();
    private bool _revoked;

    private StdEnvHostSnapshotV1(Dictionary<string, string> snapshot,
                                 string[] allow, string[] deny, bool coarseCapability)
    {
        _snapshot = snapshot;
        _allow = allow;
        _deny = deny;
        _coarseEnvironmentCapability = coarseCapability;
    }

    internal static StdEnvHostSnapshotV1 Create(
        IReadOnlyDictionary<string, string> trustedValues,
        IReadOnlyList<string> allow,
        IReadOnlyList<string> deny,
        bool coarseEnvironmentCapability)
    {
        ArgumentNullException.ThrowIfNull(trustedValues);
        string[] grants = CopyRules(allow);
        string[] denies = CopyRules(deny);
        if (trustedValues.Count > 512) throw new ArgumentException("ENV_INVALID_SNAPSHOT");
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in trustedValues.Keys)
        {
            if (!ValidName(name) || !coarseEnvironmentCapability || !Granted(name, grants, denies))
                continue;
            if (!trustedValues.TryGetValue(name, out string? value) ||
                value is null || !ValidValue(value))
                throw new ArgumentException("ENV_INVALID_SNAPSHOT");
            snapshot.Add(name, value);
        }
        return new StdEnvHostSnapshotV1(snapshot, grants, denies, coarseEnvironmentCapability);
    }

    internal Lookup Get(string name)
    {
        if (!ValidName(name)) throw new ArgumentException("ENV_INVALID_NAME");
        lock (_gate)
        {
            if (_revoked || !_coarseEnvironmentCapability || !Granted(name, _allow, _deny))
                throw new UnauthorizedAccessException("ENV_PERMISSION_DENIED");
            return _snapshot.TryGetValue(name, out string? value)
                ? new Lookup(true, value) : new Lookup(false, null);
        }
    }

    internal bool Has(string name) => Get(name).IsPresent;

    /// <summary>Isolate retirement clears retained guest-visible value references.</summary>
    internal bool Revoke()
    {
        lock (_gate)
        {
            if (_revoked) return false;
            _revoked = true;
            _snapshot.Clear();
            return true;
        }
    }

    private static string[] CopyRules(IReadOnlyList<string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > 128) throw new ArgumentException("ENV_INVALID_POLICY");
        var rules = new string[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            string rule = source[i];
            if (rule is null ||
                !(rule == "*" || ValidName(rule) ||
                  (rule.EndsWith('*') && ValidName(rule[..^1]))))
                throw new ArgumentException("ENV_INVALID_POLICY");
            rules[i] = rule;
        }
        return rules;
    }

    private static bool Granted(string name, string[] allow, string[] deny)
    {
        bool admitted = false;
        foreach (string rule in allow)
            if (Matches(name, rule)) { admitted = true; break; }
        if (!admitted) return false;
        foreach (string rule in deny)
            if (Matches(name, rule)) return false;
        return true;
    }

    private static bool Matches(string key, string rule)
    {
        if (rule == "*") return true;
        if (rule.EndsWith('*')) return key.StartsWith(rule[..^1], StringComparison.Ordinal);
        return key == rule;
    }

    private static bool ValidName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool alpha = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            bool digit = c is >= '0' and <= '9';
            if (!alpha && c != '_' && (i == 0 || !digit)) return false;
        }
        return true;
    }

    private static bool ValidValue(string value)
    {
        if (value.Length > 16384) return false;
        int bytes = 0;
        for (int i = 0; i < value.Length; i++)
        {
            int scalar = value[i];
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return false;
                scalar = char.ConvertToUtf32(value[i], value[++i]);
            }
            else if (char.IsLowSurrogate(value[i])) return false;
            bytes += scalar < 0x80 ? 1 : scalar < 0x800 ? 2 : scalar < 0x10000 ? 3 : 4;
            if (bytes > 16384) return false;
        }
        return true;
    }
}
