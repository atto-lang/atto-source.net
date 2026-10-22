using System.Threading;
using Ores.Net.Runtime;

internal static class ActorScratchCarrierSelfTest
{
    private static void Check(bool yes, string why)
    {
        if (!yes) throw new Exception("actor scratch carrier: " + why);
    }

    private static bool Denied(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }

    public static void Run()
    {
        var alice = new ActorMailbox(7101).CurrentRef;
        var bob = new ActorMailbox(7102).CurrentRef;
        using var a = new ActorTurnScratch(alice, 256);
        using var b = new ActorTurnScratch(bob, 256);

        var turn = a.BeginTurn(alice);
        var storage = a.Allocate(alice, turn, 8);
        Check(storage.HasValue, "serial actor's first native allocation");
        Check(a.Write(alice, turn, storage.Value, new byte[] { 1, 2, 3, 4 }),
            "owner carrier can initialize region");

        // Two actors must never hold active serial turns on the same carrier;
        // pooling requires leave+admit rather than nested execution.
        Check(Denied(() => b.BeginTurn(bob)), "nested actor turn on same carrier");

        bool foreignRead = false;
        bool foreignWrite = false;
        bool foreignAllocate = false;
        bool foreignEndDenied = false;
        bool foreignBeginDenied = false;
        Exception? unexpected = null;
        var stolen = new Thread(() =>
        {
            try
            {
                Span<byte> buffer = stackalloc byte[4];
                foreignRead = a.Read(alice, turn, storage.Value, buffer);
                foreignWrite = a.Write(alice, turn, storage.Value, new byte[] { 9 });
                foreignAllocate = a.Allocate(alice, turn, 8).HasValue;
                foreignEndDenied = Denied(() => a.EndTurn(alice, turn));
                foreignBeginDenied = Denied(() => a.BeginTurn(alice));
            }
            catch (Exception failure) { unexpected = failure; }
        });
        stolen.Start();
        Check(stolen.Join(10_000), "foreign carrier did not complete");
        if (unexpected != null) throw unexpected;
        Check(!foreignRead && !foreignWrite && !foreignAllocate,
            "copied owner/turn tokens must not grant physical carrier access");
        Check(foreignEndDenied && foreignBeginDenied,
            "foreign thread cannot steal or terminate admitted serial turn");
        Span<byte> original = stackalloc byte[4];
        Check(a.Read(alice, turn, storage.Value, original)
              && original.SequenceEqual(new byte[] {1, 2, 3, 4}),
              "stolen carrier attempt must not mutate native bytes");

        a.EndTurn(alice, turn);
        Check(!a.Read(alice, turn, storage.Value, original),
            "completed turn's token remains stale");
        var secondTurn = b.BeginTurn(bob);
        Check(b.Allocate(bob, secondTurn, 16).HasValue,
            "shared carrier may be readmitted to a second actor after leave");
        b.EndTurn(bob, secondTurn);
        var thirdTurn = a.BeginTurn(alice);
        Check(!a.Read(alice, thirdTurn, storage.Value, original),
            "old native scratch handle cannot revive on later serial turn");
        a.EndTurn(alice, thirdTurn);
        Console.WriteLine("PASS .NET serial scratch carrier thread fencing and turn handoff");
    }
}
