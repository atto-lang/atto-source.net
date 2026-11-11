using System;
using Ores.Net.Runtime;

// Host-runtime proof only; NOT compiled AttoLang guest, NativeAOT or GC certification.
internal static class ActorMemoryE2EV1SelfTest
{
    private static void Require(bool okay, string why)
    {
        if (!okay) throw new Exception("ACTOR_MEMORY_HOST_V1_FAILED: " + why);
    }

    public static void Run()
    {
        using var actor = new ActorNativeArena(4096, 8);
        using var foreignActor = new ActorNativeArena(4096, 8);
        ulong state = actor.Allocate(16);
        ulong foreign = foreignActor.Allocate(16);
        Require(state != 0 && foreign != 0, "actor-owned persistent allocations");
        Require(actor.WriteInt64(state, 0, 42), "initial persistent write");
        Require(!actor.WriteInt64(foreign, 0, 999), "foreign actor access rejected");

        Span<ulong> stackOwners = stackalloc ulong[2];
        // Warm the runtime API before inspecting allocation bytes.
        for (int i = 0; i < 128; i++)
        {
            var warm = new ActorNativeArena.CleanupScope(actor, stackOwners);
            ulong ephemeral = warm.Allocate(16);
            Require(ephemeral != 0, "warmup allocation");
            warm.Dispose();
            Require(warm.CleanupOk, "warmup deterministic cleanup");
        }

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
        {
            Require(actor.ReadInt64(state) == 42 + i,
                "persistent value survives simulated turn boundary");
            stackOwners.Clear();
            var turn = new ActorNativeArena.CleanupScope(actor, stackOwners);
            ulong ephemeral = turn.Allocate(16, 8);
            Require(ephemeral != 0, "temporary owner admission");
            Require(actor.ReadInt64(ephemeral) == 0, "recycled storage zeroized");
            Require(actor.WriteInt64(ephemeral, 0, i), "temporary payload write");
            Require(actor.WriteInt64(state, 0, 43L + i), "persistent state mutation");
            turn.Dispose();
            Require(turn.CleanupOk && actor.LiveAllocations == 1,
                "temporary owner dropped on simulated turn exit");
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(delta == 0, "managed allocation bytes on warmed host-only turn loop");

        ulong stale = actor.Allocate(8);
        Require(stale != 0 && actor.Drop(stale), "explicit drop");
        ulong reused = actor.Allocate(8);
        Require(reused != 0 && reused != stale, "generation changes on reuse");
        Require(actor.ReadInt64(reused) == 0, "reused native memory zeroed");
        Require(!actor.WriteInt64(stale, 0, 12), "stale handle rejected");
        try
        {
            _ = actor.ReadInt64(stale);
            throw new Exception("stale handle read unexpectedly succeeded");
        }
        catch (InvalidOperationException) { }
        Require(actor.Drop(reused) && actor.Drop(state), "final owner drops");
        Require(actor.LiveAllocations == 0 && actor.UsedBytes == 0,
            "all guest payload memory reclaimed without CLR collection");
        actor.Dispose();
        Require(actor.IsDisposed && actor.Allocate(8) == 0,
            "disposed actor heap never admits another allocation");
        Console.WriteLine("ACTOR_MEMORY_E2E_V1_HOST_ONLY=PASS");
        Console.WriteLine("ACTOR_MEMORY_E2E_V1_COMPILED_GUEST=NOT_EXECUTED");
    }
}
