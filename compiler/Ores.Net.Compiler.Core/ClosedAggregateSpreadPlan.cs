using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Checked, scalar-only CLOSED ROW type inference for future source aggregate
/// lowering. This data structure grants NO CIL emission, heap allocation,
/// field-borrow, or actor-domain ownership authority.
/// </summary>
public enum ClosedAggregateKind { ExactStruct, ReadonlyRecord }

public sealed record ClosedAggregateField(string Name, string Type);
public sealed record ClosedAggregateWrite(int Element, string Name, string Type, bool FromSpread);
public sealed record ClosedAggregateShape(ClosedAggregateKind Kind,
    IReadOnlyList<ClosedAggregateField> Fields);

public sealed class ClosedAggregateSpreadPlan
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ClosedAggregateKind _kind;
    private readonly List<ClosedAggregateWrite> _writes = new();
    private readonly HashSet<string> _explicitNames = new(StringComparer.Ordinal);
    private int _elements;

    public ClosedAggregateSpreadPlan(ClosedAggregateKind kind) { _kind = kind; }
    public IReadOnlyList<ClosedAggregateWrite> SourceOrderedWrites => _writes.AsReadOnly();
    public int SourceElements => _elements;

    private static byte[] CheckedKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        byte[] encoded;
        try { encoded = StrictUtf8.GetBytes(name); }
        catch (EncoderFallbackException error)
        {
            throw new FormatException("AGGREGATE_INVALID_UTF8_FIELD", error);
        }
        if (encoded.Length is 0 or > 1024 || name.Any(char.IsControl))
            throw new FormatException("AGGREGATE_INVALID_STATIC_FIELD_NAME");
        return encoded;
    }

    private static void CheckType(string type)
    {
        if (type is not ("int" or "bool" or "string"))
            throw new FormatException("AGGREGATE_UNSUPPORTED_FIELD_TYPE: " + type);
    }

    // Unicode scalar ordering is unsigned UTF-8 byte ordering for valid UTF-8.
    private static int CompareKeys(string left, string right)
    {
        byte[] a = CheckedKey(left), b = CheckedKey(right);
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    private void CheckBudget(int additionalWrites)
    {
        if (_elements >= 1024 || additionalWrites > 8192 - _writes.Count)
            throw new FormatException("AGGREGATE_SPREAD_PLAN_BUDGET");
    }

    public void AddNamed(string name, string type)
    {
        CheckBudget(1);
        _ = CheckedKey(name);
        CheckType(type);
        if (!_explicitNames.Add(name))
            throw new FormatException("AGGREGATE_DUPLICATE_NAMED_FIELD: " + name);
        _writes.Add(new ClosedAggregateWrite(_elements++, name, type, false));
    }

    // source must be a statically checked, visible projection, never a runtime
    // dictionary or reflection result. Borrow/copy authority is separate.
    public void AddCheckedSpread(ClosedAggregateShape source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Fields);
        if (source.Fields.Count > 64)
            throw new FormatException("AGGREGATE_FIELD_BUDGET");
        CheckBudget(source.Fields.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new List<ClosedAggregateField>(source.Fields.Count);
        foreach (var f in source.Fields)
        {
            _ = CheckedKey(f.Name);
            CheckType(f.Type);
            if (!seen.Add(f.Name))
                throw new FormatException("AGGREGATE_DUPLICATE_SOURCE_FIELD");
            prepared.Add(f);
        }
        int element = _elements++;
        foreach (var f in prepared)
            _writes.Add(new ClosedAggregateWrite(element, f.Name, f.Type, true));
    }

    public ClosedAggregateShape InferShape()
    {
        var row = new SortedDictionary<string, string>(
            Comparer<string>.Create(CompareKeys));
        foreach (var write in _writes)
            row[write.Name] = write.Type;
        if (row.Count > 64)
            throw new FormatException("AGGREGATE_FIELD_BUDGET");
        return new ClosedAggregateShape(_kind,
            row.Select(f => new ClosedAggregateField(f.Key, f.Value)).ToArray());
    }
}
