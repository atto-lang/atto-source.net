using Ores.Net.Compiler;
using static Ores.Net.Compiler.ReadonlyShapeProjectionV1;

internal static class ReadonlyShapeProjectionV1Tests
{
    private static Field F(string name, Scalar type, int slot) =>
        new(name, type, slot, true, true, true, false);
    private static Source S(Origin kind, params Field[] fields) =>
        new(kind, 7, 1, 9, false, fields);
    private static readonly RequiredField[] WantsX = [new("x", Scalar.I64)];
    private static Plan P(Source source) => Prove(source, WantsX);
    private static void Check(bool ok, string id)
    {
        if (!ok) throw new Exception("readonly shape projection failure: " + id);
    }
    private static void Reject(Action a, string id)
    {
        try { a(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("readonly shape should reject: " + id);
    }

    public static void Run()
    {
        var a = P(S(Origin.Class, F("x", Scalar.I64, 0)));
        Check(a.GetField("x").SourceSlot == 0, "SP001");
        var b = P(S(Origin.Class, F("padding", Scalar.I64, 0), F("x", Scalar.I64, 1)));
        Check(b.GetField("x").SourceSlot == 1, "SP002");
        long[] aSlots = [42L];
        long[] bSlots = [10L, 42L];
        Check(aSlots[a.GetField("x").SourceSlot] +
              bSlots[b.GetField("x").SourceSlot] == 84, "host oracle 84");
        Check(a.SourceNominalId == 7 && b.LoanId == 9, "source identity and scoped loan");
        Check(P(S(Origin.Record, F("pad", Scalar.I64, 0), F("x", Scalar.I64, 1)))
                  .GetField("x").SourceSlot == 1, "SP003 record");
        Check(P(S(Origin.Struct, F("pad", Scalar.I64, 0), F("x", Scalar.I64, 1)))
                  .GetField("x").SourceSlot == 1, "SP003 struct");
        Reject(() => P(S(Origin.Class, new Field("x", Scalar.I64, 0, false, true, true, false))), "SP004");
        Reject(() => P(S(Origin.Class, F("y", Scalar.I64, 0))), "SP005");
        Reject(() => P(S(Origin.Class, F("x", Scalar.Bool, 0))), "SP006");
        Reject(() => P(new Source(Origin.Class, 7, 1, 9, true, [F("x", Scalar.I64, 0)])), "SP007");
        Reject(() => P(S(Origin.Class, new Field("x", Scalar.I64, 0, true, true, true, true))), "SP007 field");
        Reject(() => P(S(Origin.Map, F("x", Scalar.I64, 0))), "SP008 map");
        Reject(() => P(S(Origin.Actor, F("x", Scalar.I64, 0))), "SP008 actor");
        Reject(() => P(S(Origin.Class, new Field("x", Scalar.I64, 0, true, false, true, false))), "SP009");
        Reject(() => P(new Source(Origin.Class, 7, 0, 9, false, [F("x", Scalar.I64, 0)])), "SP010");
        Reject(() => P(new Source(Origin.Class, 7, 1, 0, false, [F("x", Scalar.I64, 0)])), "SP011");
        Reject(() => P(S(Origin.Class, F("x", Scalar.I64, 0), F("x", Scalar.I64, 1))), "SP012");
        Reject(() => P(S(Origin.Class, F("x", Scalar.I64, 0), F("y", Scalar.I64, 0))), "SP013");
        Reject(() => Prove(S(Origin.Class, F("x", Scalar.I64, 0)), []), "SP014");
        Reject(() => P(new Source(Origin.Class, 0, 1, 9, false, [F("x", Scalar.I64, 0)])), "SP015");
        Reject(() => Prove(S(Origin.Class, F("x", Scalar.I64, 0)),
            [new("x", Scalar.I64), new("x", Scalar.I64)]), "SP016");
        Check(a.Fields is System.Collections.ObjectModel.ReadOnlyCollection<FieldWitness>,
            "proof does not expose mutable List");
        Console.WriteLine("readonly shape NET HOST_WITNESS 16/16; target i64 oracle 84; guest CIL/AOT NOT_EXECUTED");
    }
}
