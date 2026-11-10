using Ores.Net.Runtime;

internal static class ActorNativeAggregateStoreTests
{
    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("Native aggregate failed: " + why);
    }

    private static void Reject(Action action, string why)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (IndexOutOfRangeException) { return; }
        throw new Exception("Native aggregate accepted invalid operation: " + why);
    }

    public static void Run()
    {
        using var owner = new ActorNativeAggregateStore(512, 12);
        using var neighbor = new ActorNativeAggregateStore(512, 12);

        Span<long> elements = stackalloc long[] { 11, 23, 42 };
        ulong tuple = owner.CreateTuple(elements);
        ulong otherActorTuple = neighbor.CreateTuple(elements);
        Require(tuple != 0 && otherActorTuple != 0, "native tuple allocation");
        Require(owner.Describe(tuple) == (NativeAggregateKind.Tuple, 3, 0L),
            "compiled tuple shape carried out of managed heap");
        Require(owner.Read(tuple, NativeAggregateKind.Tuple, 2) == 42, "native tuple projection");
        Reject(() => owner.Read(otherActorTuple, NativeAggregateKind.Tuple, 0),
            "foreign actor tuple handle");
        Reject(() => owner.Read(tuple, NativeAggregateKind.Pair, 0),
            "tuple and pair have distinct nominal tags");
        Reject(() => owner.Read(tuple, NativeAggregateKind.Tuple, 3), "tuple bounds");
        Require(!owner.TryWriteArray(tuple, 0, 99), "immutable tuple write denied");
        Require(owner.Read(tuple, NativeAggregateKind.Tuple, 0) == 11,
            "immutable payload preserved");

        ulong pair = owner.CreatePair(11, 23);
        ulong entry = owner.CreateEntry(11, 23);
        Require(pair != 0 && entry != 0, "nominal pair and entry allocations");
        Require(owner.Read(pair, NativeAggregateKind.Pair, 0) == 11,
            "pair first positional slot");
        Require(owner.Read(entry, NativeAggregateKind.Entry, 1) == 23,
            "entry value positional slot");
        Reject(() => owner.Read(entry, NativeAggregateKind.Pair, 1),
            "entry must not alias pair without explicit type equivalence");
        Reject(() => owner.Read(pair, NativeAggregateKind.Entry, 0),
            "pair must not alias entry without explicit type equivalence");

        ulong record = owner.CreateRecord(17, elements);
        Require(record != 0, "native readonly record allocation");
        Require(owner.Read(record, NativeAggregateKind.Record, 1, expectedShapeId: 17) == 23,
            "checked nominal record projection");
        Reject(() => owner.Read(record, NativeAggregateKind.Record, 1, expectedShapeId: 18),
            "record schema mismatch");
        Reject(() => owner.Read(record, NativeAggregateKind.Tuple, 1),
            "record and tuple nominal disjointness");
        Require(!owner.TryWriteArray(record, 1, 5), "readonly record mutation denied");

        ulong array = owner.CreateArray(elements);
        Require(array != 0 && owner.TryWriteArray(array, 0, 7), "writable native array");
        Require(owner.Read(array, NativeAggregateKind.Array, 0) == 7,
            "native array mutation visible");
        Require(!owner.TryWriteArray(array, -1, 8) && !owner.TryWriteArray(array, 3, 8),
            "array write bounds");
        Reject(() => owner.Read(array, NativeAggregateKind.Tuple, 0),
            "array cannot be treated as immutable tuple");

        Require(owner.Drop(tuple), "tuple drop");
        Require(!owner.Drop(tuple), "double drop");
        Reject(() => owner.Read(tuple, NativeAggregateKind.Tuple, 0), "stale handle");
        Require(!owner.TryWriteArray(otherActorTuple, 0, 7),
            "foreign actor forged mutable aggregate write fails closed");
        Require(!owner.TryWriteArray(tuple, 0, 7),
            "dropped stale tuple token cannot mutate recycled storage");
        Require(!owner.Drop(otherActorTuple), "cannot drop another actor's allocation");
        Require(neighbor.Read(otherActorTuple, NativeAggregateKind.Tuple, 2) == 42,
            "foreign actor unaffected by forged drop");

        Require(owner.Drop(pair) && owner.Drop(entry) && owner.Drop(record) &&
                owner.Drop(array) && neighbor.Drop(otherActorTuple),
            "all allocations deterministically released");
        Require(owner.LiveAggregates == 0 && owner.UsedBytes == 0 &&
                neighbor.LiveAggregates == 0, "all owned native bytes reclaimed");

        // Allocation failure must neither publish a partial object nor hold
        // a stale slot/byte budget. Free and retry after full scrub.
        using (var constrained = new ActorNativeAggregateStore(48, 1))
        {
            ulong first = constrained.CreateTuple(stackalloc long[] { 5, 6, 7 });
            Require(first != 0, "48-byte object fits 48-byte arena");
            ulong denied = constrained.CreateTuple(stackalloc long[] { 8 });
            Require(denied == 0 && constrained.LiveAggregates == 1,
                "quota exceeded without partial aggregate");
            Require(constrained.Drop(first), "quota recovery drop");
            ulong reused = constrained.CreateTuple(stackalloc long[] { 8 });
            Require(reused != 0 && reused != first, "generation fenced slot reuse");
            Require(constrained.Read(reused, NativeAggregateKind.Tuple, 0) == 8,
                "recycled native storage is not stale");
            Require(constrained.Drop(reused) && constrained.LiveAggregates == 0,
                "quota recovery completion");
        }
        using (var bounded = new ActorNativeAggregateStore(64, 1))
        {
            try
            {
                _ = bounded.CreateTuple(new long[ActorNativeAggregateStore.MaximumElements + 1]);
                throw new Exception("Oversized aggregate construction admitted");
            }
            catch (ArgumentOutOfRangeException) { }
            try
            {
                _ = bounded.CreateRecord(0, stackalloc long[] { 1 });
                throw new Exception("Missing nominal record schema admitted");
            }
            catch (ArgumentOutOfRangeException) { }
        }

        // Host-runtime allocation-free steady state is a separate property
        // from source compiler/guest execution. Exercise the real native
        // arena wrapper after JIT warmup; fail on managed payload allocation.
        using (var arena = new ActorNativeAggregateStore(64, 1))
        {
            for (int i = 0; i < 100; i++)
            {
                ulong warm = arena.CreateTuple(stackalloc long[] { i, i + 1 });
                Require(warm != 0 && arena.Read(warm, NativeAggregateKind.Tuple, 1) == i + 1,
                    "warm scalar native aggregate operations");
                Require(arena.Drop(warm), "warm native cleanup");
            }
            bool valid = true;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                ulong native = arena.CreatePair(i, i + 1);
                if (native == 0 || arena.Read(native, NativeAggregateKind.Pair, 0) != i ||
                    !arena.Drop(native))
                {
                    valid = false;
                    break;
                }
            }
            long managed = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(valid && arena.LiveAggregates == 0 && arena.UsedBytes == 0,
                "unmanaged aggregate steady-state allocations reclaimed");
            Require(managed == 0,
                "native aggregate scalar create/read/drop must not allocate managed payload");
        }

        Console.WriteLine("PASS native actor-owned tagged aggregates; kind, schema, quota and lifetime");
    }
}
