using System;
using System.Buffers.Binary;
using Atto.Compiler.Structural;

static class Program
{
    private static int _passed;
    private static FieldDescriptor Field(string name, ScalarKind type, int offset) =>
        new(name, type, offset, true, false, true);
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _passed++;
    }
    private static void Reject(string code, Action action)
    {
        try { action(); }
        catch (ShapeDiagnostic e)
        {
            Check(e.Code == code, $"wrong diagnostic {e.Code} (wanted {code})");
            return;
        }
        throw new Exception($"expected {code}");
    }

    public static int Main()
    {
        var dog = new byte[16];
        var cat = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(dog.AsSpan(0, 8), 41);
        BinaryPrimitives.WriteInt64LittleEndian(cat.AsSpan(0, 8), 5);
        BinaryPrimitives.WriteInt64LittleEndian(cat.AsSpan(8, 8), 42);
        var dogLayout = new ClassLayout("Dog", [Field("foo", ScalarKind.Int, 0)]);
        var catLayout = new ClassLayout("Cat",
            [Field("age", ScalarKind.Int, 0), Field("foo", ScalarKind.Int, 8)]);
        Check(ReadonlyShapeFieldAbi.BindInt(dogLayout, "foo", dog).ReadInt() +
              ReadonlyShapeFieldAbi.BindInt(catLayout, "foo", cat).ReadInt() == 83,
              "different nominal field offsets");
        Check(ReadonlyShapeFieldAbi.BindInt(catLayout, "foo", cat).ReadInt() == 42, "owner reuse");
        // Host-only proof that readonly structural access does not mutate
        // source storage. This is not an Atto compiler implicit-borrow test.
        var dogBefore = (byte[])dog.Clone();
        var catBefore = (byte[])cat.Clone();
        _ = ReadonlyShapeFieldAbi.BindInt(dogLayout, "foo", dog).ReadInt();
        _ = ReadonlyShapeFieldAbi.BindInt(catLayout, "foo", cat).ReadInt();
        Check(dog.AsSpan().SequenceEqual(dogBefore) && cat.AsSpan().SequenceEqual(catBefore),
            "readonly shape loan must not write its owner");
        Reject("E_TYPE", () => { _ = ReadonlyShapeFieldAbi.BindInt(catLayout, "missing", cat); });
        Reject("E_TYPE", () => {
            var l = new ClassLayout("Wrong", [Field("foo", ScalarKind.Bool, 0)]);
            _ = ReadonlyShapeFieldAbi.BindInt(l, "foo", dog);
        });
        Reject("E_PRIVATE", () => {
            var l = new ClassLayout("Private", [new FieldDescriptor("foo", ScalarKind.Int, 0, false, false, true)]);
            _ = ReadonlyShapeFieldAbi.BindInt(l, "foo", dog);
        });
        Reject("E_PRIVATE", () => {
            var l = new ClassLayout("Getter", [new FieldDescriptor("foo", ScalarKind.Int, 0, true, false, false)]);
            _ = ReadonlyShapeFieldAbi.BindInt(l, "foo", dog);
        });
        Reject("E_PRIVATE", () => {
            var l = new ClassLayout("Protected", [new FieldDescriptor("foo", ScalarKind.Int, 0, true, true, true)]);
            _ = ReadonlyShapeFieldAbi.BindInt(l, "foo", dog);
        });
        Reject("E_STATE_BORROW", () => {
            var l = new ClassLayout("Actor", [Field("foo", ScalarKind.Int, 0)], true);
            _ = ReadonlyShapeFieldAbi.BindInt(l, "foo", dog);
        });
        Reject("E_BOUNDS", () => {
            var l = new ClassLayout("Bounds", [Field("foo", ScalarKind.Int, 24)]);
            _ = ReadonlyShapeFieldAbi.BindInt(l, "foo", dog);
        });
        Reject("E_DUPLICATE", () => {
            _ = new ClassLayout("Duplicate", [Field("foo", ScalarKind.Int, 0), Field("foo", ScalarKind.Int, 8)]);
        });
        Reject("E_LAYOUT", () => {
            _ = new ClassLayout("Invalid", [Field("foo", ScalarKind.Int, 1)]);
        });
        Reject("E_MOVED", () => { _ = ReadonlyShapeFieldAbi.BindInt(dogLayout, "foo", dog, true); });
        // Malformed compiler-owned layout metadata must fail closed before
        // establishing a borrowed field view (host-only ABI regression).
        Reject("E_LAYOUT", () => {
            _ = new ClassLayout("NegativeOffset", [Field("foo", ScalarKind.Int, -8)]);
        });
        Reject("E_LAYOUT", () => {
            _ = new ClassLayout("EmptyName", [Field("", ScalarKind.Int, 0)]);
        });
        Reject("E_LAYOUT", () => {
            _ = new ClassLayout("NullDescriptor", new FieldDescriptor[] { null! });
        });
        Reject("E_SOURCE_LIMIT", () => {
            var fields = new FieldDescriptor[33];
            for (var i = 0; i < fields.Length; i++)
                fields[i] = Field($"field_{i}", ScalarKind.Int, i * 8);
            _ = new ClassLayout("OverLimit", fields);
        });
        Reject("E_BOUNDS", () => {
            _ = ReadonlyShapeFieldAbi.BindInt(dogLayout, "foo", new byte[7]);
        });
        Reject("E_BOUNDS", () => {
            var tooFar = new ClassLayout("VeryFar", [Field("foo", ScalarKind.Int, int.MaxValue - 7)]);
            _ = ReadonlyShapeFieldAbi.BindInt(tooFar, "foo", dog);
        });
        // Source metadata must be immutable after capture, despite mutable
        // arrays being supplied by the compiler host.
        var mutableDescriptors = new[] { Field("foo", ScalarKind.Int, 0) };
        var snapshot = new ClassLayout("Snapshot", mutableDescriptors);
        mutableDescriptors[0] = Field("replaced", ScalarKind.Int, 8);
        Check(ReadonlyShapeFieldAbi.BindInt(snapshot, "foo", dog).ReadInt() == 41,
              "class field metadata must be defensively copied");
        Console.WriteLine($"DOTNET_SHAPE_FIELD_WITNESS_PASS checks={_passed}");
        return 0;
    }
}
