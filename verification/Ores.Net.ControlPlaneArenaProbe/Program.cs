using Ores.Net.Runtime;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("failed: " + message);
}
static void Denied(Action action)
{
    bool rejected = false;
    try { action(); }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { rejected = true; }
    if (!rejected) throw new Exception("control ref must fail closed");
}

Check(ControlPlaneArena32.Abi == "control_generation12_slot20_v1", "actual 12-bit generation / 20-bit slot control ABI");
Check(ControlPlaneArena32.SlotBits == 20 && ControlPlaneArena32.GenerationBits == 12 &&
      ControlPlaneArena32.SlotBits + ControlPlaneArena32.GenerationBits == 32,
      "control Ref32 bit partition must not be mislabeled");
Check(ControlPlaneArena32.RecordBytes == 32, "physical record width");
Check(ControlPlaneArena32.RequiredNativeBytes(1) == 48, "12-byte slot metadata is padded to 16");
Check(ControlPlaneArena32.RequiredNativeBytes(2) == 88, "two 12-byte slots require 24 native metadata bytes");
Check(ControlPlaneArena32.RequiredNativeBytes(3) == 136, "three slots pad 36 metadata bytes to 40");
long syntheticIssuer = 0;
Check(ControlPlaneArena32.ClaimIssuer(ref syntheticIssuer) == 1 && syntheticIssuer == 1,
      "first issuer reservation is monotone");
syntheticIssuer = (long)uint.MaxValue - 1;
Check(ControlPlaneArena32.ClaimIssuer(ref syntheticIssuer) == uint.MaxValue &&
      syntheticIssuer == uint.MaxValue, "last issuer issued exactly once");
Denied(() => {
    long atLimit = uint.MaxValue;
    _ = ControlPlaneArena32.ClaimIssuer(ref atLimit);
});
Denied(() => {
    long beyondLimit = long.MaxValue;
    _ = ControlPlaneArena32.ClaimIssuer(ref beyondLimit);
});
Denied(() => {
    long negative = long.MinValue;
    _ = ControlPlaneArena32.ClaimIssuer(ref negative);
});
Check(syntheticIssuer == uint.MaxValue,
      "issuer reservation must saturate and not consume global state");
using (var a = new ControlPlaneArena32(2))
using (var b = new ControlPlaneArena32(2))
{
    ulong parent = a.Allocate();
    ulong child = a.Allocate();
    ulong foreign = b.Allocate();
    Check((uint)parent == (uint)foreign, "different heaps have equal numeric 32-bit tokens");
    Check(((uint)parent & ((1u << ControlPlaneArena32.SlotBits) - 1)) == 1,
        "first control Ref32 word encodes slot 0 as low20=1");
    Check(((uint)parent >> ControlPlaneArena32.SlotBits) == 1,
        "first control Ref32 word encodes generation 1 in high12");

    Denied(() => b.ReadI64(parent, 0));
    Denied(() => a.WriteRef32(parent, foreign));
    a.WriteI64(parent, 0, 9_000_000_000L);
    Check(a.ReadI64(parent, 0) == 9_000_000_000L, "i64 data behind Ref32");
    a.WriteI64(parent, 8, long.MinValue);
    Check(a.ReadI64(parent, 8) == long.MinValue, "two i64 limbs as 128-bit payload");
    const long scalarLow = 0x0123456789abcdefL;
    const long scalarHigh = unchecked((long)0xfedcba9876543210UL);
    a.WriteI128(parent, scalarLow, scalarHigh);
    a.ReadI128(parent, out long actualLow, out long actualHigh);
    Check(actualLow == scalarLow && actualHigh == scalarHigh, "one coherent i128 payload behind four-byte Ref32");
    Denied(() => b.ReadI128(parent, out _, out _)); // foreign control domain
    // Competing carrier may update the pair. Readers must never observe
    // the first limb from one write and the second from another.
    a.WriteI128(parent, 0, ~0L);
    Exception? pairWriterFailure = null;
    var pairWriter = new System.Threading.Thread(() =>
    {
        try
        {
            for (int n = 0; n < 32768; n++)
                a.WriteI128(parent, n, ~((long)n));
        }
        catch (Exception error) { pairWriterFailure = error; }
    });
    pairWriter.Start();
    for (int n = 0; n < 32768; n++)
    {
        a.ReadI128(parent, out actualLow, out actualHigh);
        Check(actualHigh == ~actualLow, "torn 128-bit scalar while competing carrier writes");
    }
    pairWriter.Join();
    Check(pairWriterFailure is null, "i128 competing writer completed without failure");
    a.ReadI128(parent, out actualLow, out actualHigh);
    Check(actualHigh == ~actualLow, "final i128 value remains coherent");
    Console.WriteLine("PASS dotnet control i128 coherent concurrent host (NOT compiled Atto)");
    a.WriteMaxI64(parent, 8, 18);
    a.WriteMaxI64(parent, 8, 3); // racing old carrier must not regress native count
    Check(a.ReadI64(parent, 8) == 18, "native counter is monotone under reordering");
    Denied(() => a.WriteMaxI64(parent, 0, 22));
    a.WriteRef32(parent, child);
    Check(a.ReadRef32(parent) == child, "typed four-byte child ref");
    // Host-only proof: warmed synchronous unmanaged i64 operations should
    // allocate no CLR managed bytes on the current executing thread.
    for (int i = 0; i < 16_384; i++)
    {
        a.WriteI64(parent, 0, i);
        Check(a.ReadI64(parent, 0) == i, "warmup scalar parity");
    }
    long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 16_384; i++)
    {
        a.WriteI64(parent, 0, i);
        if (a.ReadI64(parent, 0) != i) throw new Exception("hotpath scalar mismatch");
    }
    long afterBytes = GC.GetAllocatedBytesForCurrentThread();
    Check(afterBytes == beforeBytes, "warmed CLR control hotpath must allocate zero managed bytes");
    Console.WriteLine("PASS dotnet control host warmed i64 read/write managed-byte delta=0");
    // The pair test uses out stack/value locals rather than an allocated array.
    for (int i = 0; i < 8192; i++)
    {
        a.WriteI128(parent, i, ~((long)i));
        a.ReadI128(parent, out actualLow, out actualHigh);
        if (actualLow != i || actualHigh != ~((long)i))
            throw new Exception("warmed i128 mismatch");
    }
    long beforePair = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 8192; i++)
    {
        a.WriteI128(parent, i, ~((long)i));
        a.ReadI128(parent, out actualLow, out actualHigh);
        if (actualLow != i || actualHigh != ~((long)i))
            throw new Exception("coherent i128 mismatch");
    }
    Check(GC.GetAllocatedBytesForCurrentThread() == beforePair,
        "warmed i128 control host path must allocate zero CLR bytes");
    Console.WriteLine("PASS dotnet control host warmed i128 pair managed-byte delta=0");
    Denied(() => a.WriteI64(parent, 16, 1));
    Denied(() => { _ = a.Allocate(); });
    a.Drop(child);
    Denied(() => a.ReadI64(child, 0));
    Denied(() => a.ReadRef32(parent));
    ulong reused = a.Allocate();
    Check(reused != child, "ABA-fenced reuse");
    a.WriteRef32(parent, reused);
    Check(a.ReadRef32(parent) == reused, "new generation");
    a.Drop(parent);
    a.Drop(reused);
    Denied(() => a.Drop(reused));
}
using (var exhausted = new ControlPlaneArena32(1))
{
    ulong current = exhausted.Allocate();
    for (int i = 1; i < 4095; i++)
    {
        exhausted.Drop(current);
        current = exhausted.Allocate();
    }
    uint generation = (uint)current >> ControlPlaneArena32.SlotBits;
    Check(generation == (1u << ControlPlaneArena32.GenerationBits) - 1,
        "generation reaches 12-bit max before permanent retirement");
    Check(((uint)current & 0x80000000u) != 0,
        "high-bit control Ref32 word must remain unsigned");
    exhausted.Drop(current);
    Denied(() => { _ = exhausted.Allocate(); });
}
var stopped = new ControlPlaneArena32(1);
ulong old = stopped.Allocate();
stopped.Dispose();
stopped.Dispose();
Check(stopped.ReservedBytes == 0, "native reservation disappears on dispose");
Denied(() => stopped.ReadI64(old, 0));
Console.WriteLine("PASS control-plane-ref32 dotnet unmanaged host 12 guarded scenarios; NO compiled Atto guest or zero-GC proof");
