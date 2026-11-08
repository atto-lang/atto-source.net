using System;
using System.Collections.Generic;

namespace Ores.Net.Runtime;

/// <summary>
/// Compiler-supplied public metadata projection for closed-world NativeAOT.
/// Not CLR reflection; never walks live objects, private state or actor arenas.
/// This does NOT implement source type_of, visibility checking or generic
/// closure by itself. Those require a trusted typed compiler at the call site.
/// </summary>
public enum AotPublicFieldKind { Int, Bool, Text }

public sealed record AotPublicField(string Name, AotPublicFieldKind Kind);
public sealed record AotPublicMethod(string Name, int Arity);

public sealed class AotPublicTypeDescriptor
{
    // Bound untrusted metadata enumerations before they can exhaust a worker.
    public const int MaxFields = 128;
    public const int MaxMethods = 128;
    public string TypeId { get; }
    public IReadOnlyList<AotPublicField> Fields { get; }
    public IReadOnlyList<AotPublicMethod> Methods { get; }

    public AotPublicTypeDescriptor(
        string typeId, IEnumerable<AotPublicField> fields, IEnumerable<AotPublicMethod> methods)
    {
        if (!AotPublicTypeCatalog.IsCanonicalName(typeId))
            throw new ArgumentException("invalid closed type ID", nameof(typeId));
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(methods);
        TypeId = typeId;

        var fieldCopy = new List<AotPublicField>();
        var methodCopy = new List<AotPublicMethod>();
        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (fieldCopy.Count >= MaxFields)
                throw new ArgumentException("public field descriptor quota exceeded", nameof(fields));
            if (field is null || !AotPublicTypeCatalog.IsCanonicalName(field.Name) ||
                !Enum.IsDefined(field.Kind) || !seenFields.Add(field.Name))
                throw new ArgumentException("invalid/duplicate public field descriptor", nameof(fields));
            // Metadata kinds cannot carry private actor addresses, unmanaged
            // pointers, runtime handles or dynamic object references.
            fieldCopy.Add(field);
        }
        foreach (var method in methods)
        {
            if (methodCopy.Count >= MaxMethods)
                throw new ArgumentException("public method descriptor quota exceeded", nameof(methods));
            if (method is null || !AotPublicTypeCatalog.IsCanonicalName(method.Name) ||
                method.Arity < 0 || method.Arity > 16 ||
                !seenMethods.Add(method.Name + "/" + method.Arity))
                throw new ArgumentException("invalid/duplicate public method descriptor", nameof(methods));
            methodCopy.Add(method);
        }
        Fields = fieldCopy.AsReadOnly();
        Methods = methodCopy.AsReadOnly();
    }
}

/// <summary>
/// Closed metadata table supplied by a trusted AOT compiler. Never performs
/// assembly scanning, arbitrary type discovery, reflection fallback, runtime
/// generic instantiation or dynamic proxy creation. The compiler MUST validate
/// public visibility and root only statically reachable descriptors.
/// </summary>
public sealed class AotPublicTypeCatalog
{
    public const int MaxTypes = 1024;
    private readonly SortedDictionary<string, AotPublicTypeDescriptor> descriptors =
        new(StringComparer.Ordinal);

    public AotPublicTypeCatalog(IEnumerable<AotPublicTypeDescriptor> publishedTypes)
    {
        ArgumentNullException.ThrowIfNull(publishedTypes);
        foreach (var entry in publishedTypes)
        {
            if (descriptors.Count >= MaxTypes)
                throw new ArgumentException("public type descriptor quota exceeded", nameof(publishedTypes));
            if (entry is null || !descriptors.TryAdd(entry.TypeId, entry))
                throw new ArgumentException("duplicate/null type descriptor", nameof(publishedTypes));
        }
    }

    public int Count => descriptors.Count;

    public bool TryGetPublicType(string typeId, out AotPublicTypeDescriptor? descriptor)
    {
        if (!IsCanonicalName(typeId))
        {
            descriptor = null;
            return false;
        }
        return descriptors.TryGetValue(typeId, out descriptor);
    }

    // ASCII stable symbols only. No runtime type names, assembly-qualified
    // names, generic specializations, pointer syntax, control chars or
    // Unicode confusables. Dotted namespace segments must be nonempty.
    public static bool IsCanonicalName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        bool start = true;
        foreach (char c in name)
        {
            if (c == '.' && !start) { start = true; continue; }
            if (start)
            {
                if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
                    return false;
                start = false;
            }
            else if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                       >= '0' and <= '9' or '_')) return false;
        }
        return !start;
    }
}
