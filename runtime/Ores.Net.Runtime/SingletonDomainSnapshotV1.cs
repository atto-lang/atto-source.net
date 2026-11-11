using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Runtime;

/// <summary>
/// Host-only application-domain singleton identity and immutable snapshots.
/// NOT compiled AttoLang guest singleton, cross-isolate shared memory, or an
/// actor-private heap alias. Trusted compiler/linker supplies canonical keys.
/// </summary>
public sealed class SingletonDomainSnapshotV1
{
    public readonly record struct Key(string Namespace, string Module, string Declaration)
    {
        internal void Validate()
        {
            ValidateName(Namespace, "namespace");
            ValidateName(Module, "module");
            ValidateName(Declaration, "declaration");
        }
    }

    public sealed class NominalSymbol
    {
        internal NominalSymbol(string scope, string name)
        {
            Scope = scope;
            Name = name;
        }

        public string Scope { get; }
        public string Name { get; }
        public bool LogicalEquals(NominalSymbol other) =>
            other is not null && Scope == other.Scope && Name == other.Name;
    }

    private sealed record Cell(string Fingerprint, object Snapshot);
    private readonly object _gate = new();
    private readonly Dictionary<Key, Cell> _snapshots = new();
    private readonly Dictionary<(string, string), NominalSymbol> _symbols = new();
    private readonly int _maxSingletons;
    private readonly int _maxSymbols;
    private int _symbolBytes;

    public SingletonDomainSnapshotV1(int maxSingletons = 4096, int maxSymbols = 4096)
    {
        if (maxSingletons < 1 || maxSingletons > 65536
            || maxSymbols < 1 || maxSymbols > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxSingletons));
        _maxSingletons = maxSingletons;
        _maxSymbols = maxSymbols;
    }

    /// <summary>
    /// Produces one canonical deeply frozen object per checked declaration key.
    /// An ABI-incompatible re-declaration fails closed; the original snapshot
    /// is never overwritten. No caller-supplied initializer callback is run.
    /// </summary>
    public object PublishConst(Key key, Dictionary<string, object?> literalFields)
    {
        key.Validate();
        ArgumentNullException.ThrowIfNull(literalFields);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var budget = 0;
        object frozen = Freeze(literalFields, seen, ref budget, 0)!;
        string schema = Fingerprint(frozen);
        lock (_gate)
        {
            if (_snapshots.TryGetValue(key, out var current))
            {
                if (!StringComparer.Ordinal.Equals(current.Fingerprint, schema))
                    throw new InvalidOperationException("singleton declaration schema mismatch");
                return current.Snapshot;
            }
            if (_snapshots.Count >= _maxSingletons)
                throw new InvalidOperationException("singleton identity quota exceeded");
            _snapshots.Add(key, new Cell(schema, frozen));
            return frozen;
        }
    }

    /// <summary>No mutable guest region can be opened before runtime/borrow proofs.</summary>
    public object OpenMutableRegion() =>
        throw new NotSupportedException("ATTO-SINGLETON-MUTABLE-GUEST-UNSUPPORTED");

    /// <summary>Interning is bounded and nominal; strings are never Symbols.</summary>
    public NominalSymbol InternSymbol(string scope, string name)
    {
        if (scope is not ("core" or "process" or "private"))
            throw new ArgumentException("unknown symbol scope");
        ValidateName(name, "symbol");
        lock (_gate)
        {
            var key = (scope, name);
            if (_symbols.TryGetValue(key, out var existing)) return existing;
            int bytes = Encoding.UTF8.GetByteCount(name);
            if (_symbols.Count >= _maxSymbols || _symbolBytes > 1048576 - bytes)
                throw new InvalidOperationException("symbol quota exceeded");
            var created = new NominalSymbol(scope, name);
            _symbols.Add(key, created);
            _symbolBytes += bytes;
            return created;
        }
    }

    /// <summary>
    /// Decode only trusted/previously published catalog entries. Untrusted
    /// transport must never mint user/private symbols by naming them.
    /// </summary>
    public NominalSymbol ResolveSymbol(string scope, string name, bool trusted = false)
    {
        if (scope == "private" && !trusted)
            throw new UnauthorizedAccessException("private symbol transport denied");
        if (scope is not ("core" or "process" or "private"))
            throw new ArgumentException("unknown symbol scope");
        ValidateName(name, "symbol");
        lock (_gate)
        {
            if (!_symbols.TryGetValue((scope, name), out var result))
                throw new UnauthorizedAccessException("unknown symbol cannot be interned from wire");
            return result;
        }
    }

    private static void ValidateName(string? name, string kind)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 256
            || name.Contains('\0') || name.Contains('\\')
            || name.Split('/').Any(x => x is "." or "..")
            || !StringComparer.Ordinal.Equals(name, name.Normalize(NormalizationForm.FormC))
            || name.Any(char.IsControl))
            throw new ArgumentException("invalid singleton " + kind);
    }

    private static object? Freeze(object? value, HashSet<object> seen, ref int budget, int depth)
    {
        if (++budget > 2048 || depth > 64)
            throw new ArgumentOutOfRangeException(nameof(value), "singleton snapshot budget");
        if (value is null || value is bool || value is int || value is long) return value;
        if (value is float f)
        {
            if (!float.IsFinite(f) || float.IsNegative(f) && f == 0f)
                throw new ArgumentException("nonfinite or negative zero singleton scalar");
            return f;
        }
        if (value is double d)
        {
            if (!double.IsFinite(d) || double.IsNegative(d) && d == 0d)
                throw new ArgumentException("nonfinite or negative zero singleton scalar");
            return d;
        }
        if (value is string text)
        {
            if (text.Length > 8192 || text != text.Normalize(NormalizationForm.FormC))
                throw new ArgumentException("invalid singleton scalar string");
            return text;
        }
        if (!seen.Add(value))
            throw new ArgumentException("cyclic singleton graph");
        try
        {
            if (value.GetType() == typeof(Dictionary<string, object?>))
            {
                var source = (Dictionary<string, object?>)value;
                if (source.Count > 1024) throw new ArgumentException("singleton map budget");
                var clone = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in source.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    ValidateName(pair.Key, "field");
                    if (pair.Key is "__proto__" or "constructor" or "prototype")
                        throw new ArgumentException("unsafe singleton map key");
                    clone.Add(pair.Key, Freeze(pair.Value, seen, ref budget, depth + 1));
                }
                return new ReadOnlyDictionary<string, object?>(clone);
            }
            if (value.GetType() == typeof(List<object?>))
            {
                var source = (List<object?>)value;
                if (source.Count > 1024) throw new ArgumentException("singleton list budget");
                var clone = new List<object?>(source.Count);
                foreach (object? item in source)
                    clone.Add(Freeze(item, seen, ref budget, depth + 1));
                return new ReadOnlyCollection<object?>(clone);
            }
            throw new ArgumentException("unproved mutable singleton value type");
        }
        finally
        {
            seen.Remove(value);
        }
    }

    private static string Fingerprint(object value)
    {
        var buf = new StringBuilder();
        Append(buf, value);
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(buf.ToString()));
        return Convert.ToHexStringLower(sum);
    }

    private static void Append(StringBuilder s, object? value)
    {
        switch (value)
        {
            case null: s.Append("N;"); return;
            case bool b: s.Append(b ? "B1;" : "B0;"); return;
            case int i: s.Append("I").Append(i.ToString(CultureInfo.InvariantCulture)).Append(';'); return;
            case long l: s.Append("L").Append(l.ToString(CultureInfo.InvariantCulture)).Append(';'); return;
            case float f: s.Append("F").Append(f.ToString("R", CultureInfo.InvariantCulture)).Append(';'); return;
            case double d: s.Append("D").Append(d.ToString("R", CultureInfo.InvariantCulture)).Append(';'); return;
            case string str:
                s.Append("S").Append(str.Length).Append(':').Append(str); return;
            case ReadOnlyDictionary<string, object?> map:
                s.Append("M").Append(map.Count).Append('[');
                foreach (var pair in map)
                {
                    Append(s, pair.Key);
                    Append(s, pair.Value);
                }
                s.Append(']');
                return;
            case ReadOnlyCollection<object?> list:
                s.Append("A").Append(list.Count).Append('[');
                foreach (var item in list) Append(s, item);
                s.Append(']');
                return;
            default:
                throw new ArgumentException("unsupported canonical singleton field");
        }
    }
}
