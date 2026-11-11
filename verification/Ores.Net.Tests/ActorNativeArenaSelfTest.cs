using Ores.Net.Runtime;

internal static class ActorNativeArenaSelfTest
{
    public static void Run()
    {
        using var arena = new ActorNativeArena(256, 8);
        using var other = new ActorNativeArena(256, 8);

        ulong first = arena.Allocate(24, 8);
        ulong foreign = other.Allocate(24, 8);
        Require(first != ActorNativeArena.InvalidHandle, "first native actor allocation");
        Require(foreign != ActorNativeArena.InvalidHandle, "foreign native actor allocation");
        Require(arena.WriteInt64(first, 0, 42), "native actor write");
        Require(arena.ReadInt64(first) == 42, "native actor read");
        Require(!arena.WriteInt64(foreign, 0, 7), "foreign handle fails closed");
        RejectInvalid(() => _ = arena.ReadInt64(foreign), "foreign read");

        Require(arena.Drop(first), "deterministic drop");
        Require(!arena.Drop(first), "double drop rejected");
        ulong replacement = arena.Allocate(24, 8);
        Require(replacement != ActorNativeArena.InvalidHandle && replacement != first,
            "slot reuse mints a fresh global token");
        Require(arena.ReadInt64(replacement) == 0, "recycled bytes are zeroed");
        Require(arena.Drop(replacement), "replacement drop");
        Require(arena.LiveAllocations == 0 && arena.UsedBytes == 0,
            "last owner drop recycles bump capacity");

        for (int i = 0; i < 10_000; i++)
        {
            ulong handle = arena.Allocate(16, 8);
            Require(handle != ActorNativeArena.InvalidHandle, "long-lived actor allocation");
            Require(arena.WriteInt64(handle, 0, i), "long-lived actor write");
            Require(arena.ReadInt64(handle) == i, "long-lived actor read");
            Require(arena.Drop(handle), "long-lived actor deterministic drop");
        }
        Require(arena.LiveAllocations == 0 && arena.UsedBytes == 0,
            "10k request turns leave no guest allocation live");

        Span<ulong> tracking = stackalloc ulong[4];
        var scope = new ActorNativeArena.CleanupScope(arena, tracking);
        ulong lexicalA = scope.Allocate(16, 8);
        ulong lexicalB = scope.Allocate(16, 8);
        Require(lexicalA != 0 && lexicalB != 0 && scope.Tracked == 2,
            "stack cleanup scope tracks lexical owners");
        Require(scope.Drop(lexicalA), "explicit lexical drop disarms one owner");
        Require(scope.Release(lexicalB), "move/transfer disarms lexical cleanup");
        scope.Dispose();
        Require(scope.CleanupOk && arena.LiveAllocations == 1,
            "scope keeps transferred owner alive");
        Require(arena.Drop(lexicalB), "transferred owner is dropped by new owner");

        Span<ulong> automaticTracking = stackalloc ulong[4];
        var automatic = new ActorNativeArena.CleanupScope(arena, automaticTracking);
        Require(automatic.Allocate(16) != 0 && automatic.Allocate(16) != 0,
            "automatic cleanup fixture");
        automatic.Dispose();
        Require(automatic.CleanupOk && arena.LiveAllocations == 0,
            "scope exit deterministically drops all untransferred owners");

        Span<ulong> staleTracking = stackalloc ulong[4];
        var staleScope = new ActorNativeArena.CleanupScope(arena, staleTracking);
        ulong stale = staleScope.Allocate(16);
        ulong survivor = staleScope.Allocate(16);
        Require(stale != 0 && survivor != 0, "stale cleanup fixture");
        Require(arena.Drop(stale), "out-of-band drop fixture");
        staleScope.Dispose();
        Require(!staleScope.CleanupOk && arena.LiveAllocations == 0,
            "stale tracked owner is reported without suppressing later cleanup");

        ConcurrentDisposeDoesNotRaceNativeAccess();
        RejectUnboundedNativeArenaReservation();
        PersistentRootAndTemporaryTailChurn();
        OutOfOrderDropsCoalesceNativeTail();
        SteadyStateManagedAllocationBudget();
        ActorMemoryE2EV1SelfTest.Run(); // host-only: NOT guest or NativeAOT certification
        Console.WriteLine("PASS .NET unmanaged actor arena, primitive handles, stack cleanup scopes");
    }

    private static void SteadyStateManagedAllocationBudget()
    {
        using var arena = new ActorNativeArena(256, 4);

        // Warm every path and JIT generic helper before taking a measurement.
        for (int i = 0; i < 100; i++)
        {
            ulong warm = arena.Allocate(16);
            Require(warm != ActorNativeArena.InvalidHandle, "allocation warmup");
            Require(arena.WriteInt64(warm, 0, i), "write warmup");
            Require(arena.ReadInt64(warm) == i, "read warmup");
            Require(arena.Drop(warm), "drop warmup");
        }

        bool correct = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5_000; i++)
        {
            ulong handle = arena.Allocate(16);
            if (handle == ActorNativeArena.InvalidHandle ||
                !arena.WriteInt64(handle, 0, i) ||
                arena.ReadInt64(handle) != i ||
                !arena.Drop(handle))
            {
                correct = false;
                break;
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(correct, "bounded native operations remain functional");
        Require(allocated == 0,
            "steady-state primitive native allocate/read/write/drop must not allocate CLR objects");
        Require(arena.LiveAllocations == 0 && arena.UsedBytes == 0,
            "steady-state benchmark leaves no retained actor allocation");
    }

    private static void OutOfOrderDropsCoalesceNativeTail()
    {
        using var arena = new ActorNativeArena(96, 4);
        ulong root = arena.Allocate(8);
        ulong older = arena.Allocate(40);
        ulong newer = arena.Allocate(40);
        Require(root != 0 && older != 0 && newer != 0,
            "out-of-order native tail fixture admitted");
        Require(arena.WriteInt64(root, 0, 0x234567),
            "permanent native root initialized");
        Require(arena.WriteInt64(older, 0, 0xABCD) &&
                arena.WriteInt64(newer, 0, 0xFEDC),
            "native transient values initialized");

        Require(arena.Drop(older) && arena.Drop(newer),
            "out-of-order transient drops succeed");
        ulong reused = arena.Allocate(80);
        Require(reused != 0,
            "coalesced dead native tail accepts a large replacement");
        Require(arena.ReadInt64(reused) == 0,
            "reused native bytes were scrubbed");
        Require(arena.ReadInt64(root) == 0x234567,
            "coalescing preserves live native root");
        Require(arena.Drop(reused) && arena.Drop(root),
            "coalesced tail allocations have deterministic cleanup");
    }

    private static void PersistentRootAndTemporaryTailChurn()
    {
        using var arena = new ActorNativeArena(128, 3);
        ulong root = arena.Allocate(8);
        Require(root != ActorNativeArena.InvalidHandle,
            "permanent root initialized");
        Require(arena.WriteInt64(root, 0, 0x12345678),
            "permanent root contents written");

        for (int i = 0; i < 10_000; i++)
        {
            ulong transient = arena.Allocate(64, 8);
            Require(transient != ActorNativeArena.InvalidHandle,
                "transient tail allocations cannot exhaust permanent-root arena");
            Require(arena.WriteInt64(transient, 0, i),
                "transient value write");
            Require(arena.Drop(transient),
                "transient value dropped");
            Require(arena.LiveAllocations == 1,
                "temporary drop preserves only permanent root");
            Require(arena.ReadInt64(root) == 0x12345678,
                "temporary reuse cannot overwrite live actor root");
        }

        Require(arena.Drop(root) && arena.UsedBytes == 0,
            "final root drop reclaims bump capacity");
    }

    private static void RejectUnboundedNativeArenaReservation()
    {
        bool rejected = false;
        try
        {
            using var invalid = new ActorNativeArena(
                ActorNativeArena.MaximumCapacityBytes + 1, 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Require(rejected, "oversized unmanaged reservation rejects before allocation");

        using var small = new ActorNativeArena(64, 1);
        Require(small.Allocate(64, 1) != ActorNativeArena.InvalidHandle,
            "bounded valid arena still allocates normally");
    }

    private static void ConcurrentDisposeDoesNotRaceNativeAccess()
    {
        for (int round = 0; round < 20; round++)
        {
            using var contender = new ActorNativeArena(512, 8);
            ulong handle = contender.Allocate(8);
            Require(handle != ActorNativeArena.InvalidHandle, "race fixture allocation");
            Require(contender.WriteInt64(handle, 0, round), "race fixture initialized");

            using var entered = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                entered.Set();
                for (int i = 0; i < 2_000; i++)
                {
                    try
                    {
                        long value = contender.ReadInt64(handle);
                        Require(value == round, "native read returned corrupted data");
                    }
                    catch (InvalidOperationException) when (contender.IsDisposed)
                    {
                        break;
                    }
                }
            });
            Require(entered.Wait(TimeSpan.FromSeconds(5)),
                "reader enters the concurrent disposal scenario");
            var disposer = Task.Run(contender.Dispose);
            Task.WaitAll(reader, disposer);
            Require(contender.IsDisposed && contender.LiveAllocations == 0,
                "concurrent disposal completed with no live arena allocations");
            Require(!contender.WriteByte(handle, 0, 123),
                "disposed native storage never admits writes");
            RejectInvalid(() => _ = contender.ReadInt64(handle),
                "disposed native storage never admits reads");
        }
    }

    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("ActorNativeArena FAILED: " + why);
    }

    private static void RejectInvalid(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("ActorNativeArena expected rejection: " + why);
    }
}
