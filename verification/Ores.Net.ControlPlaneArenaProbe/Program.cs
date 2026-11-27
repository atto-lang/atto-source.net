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
using (var fifo = new ControlPlaneArena32(3))
{
    for (uint generation = 1; generation <= 4095; generation++)
    for (uint slot = 1; slot <= 3; slot++)
    {
        Check(fifo.TryAllocate(out ulong h), "FIFO allocation");
        Check(((uint)h & ((1u << 20)-1)) == slot && ((uint)h >> 20) == generation, "FIFO word parity");
        Check(fifo.ReadI64(h, 0) == 0 && fifo.ReadRef32(h) == 0, "FIFO scrub");
        fifo.WriteI64(h,0,slot); fifo.Drop(h);
    }
    Check(!fifo.TryAllocate(out ulong denied) && denied == 0, "all generations retired");
}
using (var fifo = new ControlPlaneArena32(5))
{
    ulong[] held = new ulong[5];
    for (int i=0;i<5;i++) held[i]=fifo.Allocate();
    Check(!fifo.TryAllocate(out _), "full");
    for (int i=4;i>=0;i--) fifo.Drop(held[i]);
    for (int i=4;i>=0;i--)
    {
        ulong h=fifo.Allocate();
        Check(h!=held[i] && ((uint)h & ((1u<<20)-1)) == (uint)i+1, "reverse FIFO release");
        Denied(()=>fifo.ReadI64(held[0],0));
    }
}
using (var longLived = new ControlPlaneArena32(256))
{
    for (int i=0;i<1_000_000;i++)
    {
        Check(longLived.TryAllocate(out ulong h), "long reuse");
        Check(longLived.ReadI64(h,0)==0 && longLived.ReadRef32(h)==0,"scrub");
        longLived.WriteI64(h,0,i);longLived.Drop(h);
    }
    Console.WriteLine("NET_CONTROL_FIFO_PASS cycles=1000000 retirement=12285");
}
var stopped = new ControlPlaneArena32(1);
ulong old = stopped.Allocate();
stopped.Dispose();
stopped.Dispose();
Denied(() => stopped.ReadI64(old, 0));
Check(!stopped.TryAllocate(out ulong closed) && closed == 0, "closed allocation denial");
using (var full = new ControlPlaneArena32(1))
{
    Check(full.TryAllocate(out ulong held), "initial allocation");
    for (int i = 0; i < 8192; i++) Check(!full.TryAllocate(out ulong denied) && denied == 0, "warm denial");
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 16384; i++)
        if (full.TryAllocate(out ulong denied) || denied != 0) throw new Exception("quota denial failed");
    Check(GC.GetAllocatedBytesForCurrentThread() == before, "quota denial managed-byte delta");
    full.Drop(held);
}
using (var reuse = new ControlPlaneArena32(16))
{
    static void Cycle(ControlPlaneArena32 arena, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!arena.TryAllocate(out ulong h)) throw new Exception("unexpected exhaustion");
            if (arena.ReadI64(h, 0) != 0 || arena.ReadRef32(h) != 0) throw new Exception("reuse not scrubbed");
            arena.WriteI64(h, 0, i);
            if (arena.ReadI64(h, 0) != i) throw new Exception("payload mismatch");
            arena.Drop(h);
        }
    }
    Cycle(reuse, 8192);
    long before = GC.GetAllocatedBytesForCurrentThread();
    Cycle(reuse, 16384);
    Check(GC.GetAllocatedBytesForCurrentThread() == before, "allocation/drop managed-byte delta");
    Console.WriteLine("PASS dotnet control host reuse=16384 quota_denials=16384 managed_byte_delta=0");
}
Console.WriteLine("PASS control-plane-ref32 dotnet unmanaged host; NO compiled Atto guest or whole-process zero-GC proof");
