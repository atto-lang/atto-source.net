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
Denied(() => stopped.ReadI64(old, 0));
Console.WriteLine("PASS control-plane-ref32 dotnet unmanaged host 12 guarded scenarios; NO compiled Atto guest or zero-GC proof");
