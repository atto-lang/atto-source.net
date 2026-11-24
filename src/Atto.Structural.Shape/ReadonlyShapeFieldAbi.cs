using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Atto.Compiler.Structural;

/// <summary>
/// Compiler-owned scalar field metadata. This is not .atto parser/codegen.
/// Only the first synchronous readonly int64 structural loan is represented.
/// </summary>
public enum ScalarKind { Int, Bool, String, Float }

public sealed record FieldDescriptor(
    string Name, ScalarKind Type, int Offset,
    bool PublicVisible, bool ActorState, bool PlainDataField);

public sealed class ClassLayout
{
    public string NominalName { get; }
    public bool SealedActorState { get; }
    public IReadOnlyList<FieldDescriptor> Fields { get; }

    public ClassLayout(string nominalName, FieldDescriptor[] fields, bool sealedActorState = false)
    {
        ArgumentNullException.ThrowIfNull(nominalName);
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length > 32) throw new ShapeDiagnostic("E_SOURCE_LIMIT");
        var owned = (FieldDescriptor[])fields.Clone();
        for (var i = 0; i < owned.Length; i++)
        {
            if (owned[i] is null || string.IsNullOrEmpty(owned[i].Name)
                || owned[i].Offset < 0 || owned[i].Offset % 8 != 0)
                throw new ShapeDiagnostic("E_LAYOUT");
            for (var j = i + 1; j < owned.Length; j++)
                if (owned[i].Name == owned[j].Name)
                    throw new ShapeDiagnostic("E_DUPLICATE");
        }
        NominalName = nominalName;
        Fields = Array.AsReadOnly(owned);
        SealedActorState = sealedActorState;
    }
}

public sealed class ShapeDiagnostic(string code) : Exception(code)
{
    public string Code => Message;
}

/// <summary>
/// A readonly, byref-like loan. The C# compiler will not allow this field
/// address view to be boxed or stored on the managed heap.
/// </summary>
public readonly ref struct ReadonlyIntLoan
{
    private readonly ReadOnlySpan<byte> _memory;
    internal ReadonlyIntLoan(ReadOnlySpan<byte> memory) => _memory = memory;
    public long ReadInt() => BinaryPrimitives.ReadInt64LittleEndian(_memory);
}

public static class ReadonlyShapeFieldAbi
{
    /// <summary>
    /// The caller proves a live owner and borrows a class-owned byte span
    /// while a synchronous callee executes. Never casts nominal class
    /// pointers, invokes getters, or transfers a capability across actors.
    /// </summary>
    public static ReadonlyIntLoan BindInt(ClassLayout source, string fieldName,
                                          ReadOnlySpan<byte> ownerBytes, bool ownerMoved = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fieldName);
        if (ownerMoved) throw new ShapeDiagnostic("E_MOVED");
        if (source.SealedActorState) throw new ShapeDiagnostic("E_STATE_BORROW");
        FieldDescriptor? field = null;
        foreach (var candidate in source.Fields)
            if (candidate.Name == fieldName) { field = candidate; break; }
        if (field is null) throw new ShapeDiagnostic("E_TYPE");
        if (!field.PublicVisible || field.ActorState || !field.PlainDataField)
            throw new ShapeDiagnostic("E_PRIVATE");
        if (field.Type != ScalarKind.Int) throw new ShapeDiagnostic("E_TYPE");
        if (ownerBytes.Length < sizeof(long) || field.Offset > ownerBytes.Length - sizeof(long))
            throw new ShapeDiagnostic("E_BOUNDS");
        return new ReadonlyIntLoan(ownerBytes.Slice(field.Offset, sizeof(long)));
    }
}
