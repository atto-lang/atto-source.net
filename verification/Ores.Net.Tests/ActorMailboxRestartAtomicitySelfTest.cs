using Ores.Net.Runtime;
using System.Reflection;

internal static class ActorMailboxRestartAtomicitySelfTest
{
    public static void Run()
    {
        var box = new ActorMailbox(8501);
        var initial = box.CurrentRef;
        box.Send(initial, new byte[] { 1, 2, 3 });
        var field = typeof(ActorMailbox).GetField("_nextNonce", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(box, (Func<Guid>)(() => throw new InvalidOperationException("simulated failure")));
        var rejected = false;
        try { box.Restart(); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected || box.CurrentRef != initial || box.QueuedMessages != 1)
            throw new Exception("failed restart changed actor state");
        box.Send(initial, new byte[] { 4 });
        if (!box.TryReceive(out var data) || data is null || data.Length != 3 || data[0] != 1)
            throw new Exception("failed restart lost queued bytes");
        Console.WriteLine("PASS failed restart preserves generation and pending messages");
    }
}
