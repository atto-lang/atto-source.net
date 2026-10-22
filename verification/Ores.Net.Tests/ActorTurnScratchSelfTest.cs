using System.Text.Json;
using Ores.Net.Runtime;

internal static class ActorTurnScratchSelfTest
{
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new Exception("actor scratch: " + reason);
    }

    private static void Deny(Action f, string reason)
    {
        try { f(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("actor scratch did not reject " + reason);
    }

    private static void DenyWire(Action f, string reason)
    {
        try { f(); }
        catch (NotSupportedException) { return; }
        throw new Exception("scratch authority serialized: " + reason);
    }

    public static void Run()
    {
        var a = new ActorMailbox(900).CurrentRef;
        var b = new ActorMailbox(901).CurrentRef;
        var budget = new ActorNativeMemoryBudget(96);
        using var scratch = new ActorTurnScratch(a, 64, budget);
        Check(budget.ReservedBytes == 64, "reserve aggregate scratch bytes");
        Deny(() => _ = new ActorTurnScratch(b, 64, budget),
            "aggregate allocation beyond global memory budget");
        Deny(() => scratch.BeginTurn(b), "foreign owner begin");
        var turn = scratch.BeginTurn(a);
        Deny(() => scratch.BeginTurn(a), "overlapping serial actor turns");
        var root = scratch.Allocate(a, turn, 16, 16);
        Check(root.HasValue, "first aligned allocation");
        Check(scratch.Write(a, turn, root.Value, new byte[] { 3, 4, 5, 6 }),
            "write scratch buffer");
        Span<byte> buffer = stackalloc byte[4];
        Check(scratch.Read(a, turn, root.Value, buffer) &&
              buffer.SequenceEqual(new byte[] { 3, 4, 5, 6 }), "native readback");
        Deny(() => scratch.Allocate(b, turn, 8), "foreign scratch allocation");
        Check(!scratch.Allocate(a, turn, 0).HasValue &&
              !scratch.Allocate(a, turn, 8, 3).HasValue &&
              !scratch.Allocate(a, turn, 128).HasValue,
              "invalid byte and alignment limits");
        Check(!scratch.Write(a, turn, root.Value, new byte[17]),
              "out-of-bounds allocation write");
        Check(!scratch.Read(a, turn, root.Value, buffer, -1),
              "negative scratch read offset");
        Deny(() => scratch.Retire(a), "retire active scratch turn");
        Check(!scratch.Retired && budget.ReservedBytes == 64,
              "active retirement denial must not refund native bytes");

        var unused = scratch.Allocate(a, turn, 16, 8);
        Check(unused.HasValue, "second native payload");
        DenyWire(() => JsonSerializer.Serialize(turn), "scratch turn");
        DenyWire(() => JsonSerializer.Serialize(root.Value), "scratch handle");
        scratch.EndTurn(a, turn);
        Check(scratch.UsedBytes == 0, "end turn reclaims native scratch cursor");
        Check(!scratch.Read(a, turn, root.Value, buffer),
              "ended turn rejects stale handle");
        Deny(() => scratch.EndTurn(a, turn), "duplicate turn end");
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            var nextTurn = scratch.BeginTurn(a);
            var next = scratch.Allocate(a, nextTurn, 16, 8);
            Check(next.HasValue, "long-lived actor scratch must recycle");
            Check(scratch.Read(a, nextTurn, next.Value, buffer) &&
                  buffer.SequenceEqual(new byte[4]),
                  "turn reset must erase prior payload");
            Check(!scratch.Read(a, nextTurn, root.Value, buffer),
                  "previous-turn handle must not alias reused memory");
            Check(scratch.Write(a, nextTurn, next.Value, new byte[] { 9, 8, 7, 6 }),
                  "fresh turn scratch writable");
            scratch.EndTurn(a, nextTurn);
        }
        scratch.Retire(a);
        scratch.Retire(a);
        Check(scratch.Retired && budget.ReservedBytes == 0,
              "native buffer reclaimed and aggregate budget refunded once");
        Deny(() => scratch.BeginTurn(a), "restart against retired scratch");
        Console.WriteLine("PASS .NET native message scratch: no per-turn payload heap, ABA and scrub");
    }
}
