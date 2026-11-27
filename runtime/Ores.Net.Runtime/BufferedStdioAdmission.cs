using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Ores.Net.Runtime;

// Trusted serialized owner lane only. Not a guest capability or scheduler.
// Sink is independent; transfer payload once and retain charge until exit.
public sealed class BufferedStdioAdmission
{
    public readonly record struct Owner(ulong Execution, ulong Generation);
    public readonly record struct Ticket(long Descriptor, ulong Id, Owner Owner);
    public readonly record struct SinkTicket(long Descriptor, ulong Id);
    public enum Result { Accepted, Pending, Quota, Invalid, Oversize, Closed, Failed, Stale }
    private sealed record Record(ulong Id, byte[] Payload);
    private sealed record Waiter(Ticket Ticket, byte[] Payload);
    private sealed record Ready(Ticket Ticket, Result Outcome);
    private static long nextDescriptor;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly long descriptor;
    private readonly int recordsLimit, bytesLimit, waitLimit, pendingLimit;
    private readonly Queue<Record> records = new();
    private readonly List<Waiter> waiting = new();
    private readonly List<Ready> ready = new();
    private (SinkTicket Ticket, int Bytes)? active;
    private int acceptedBytes, pendingBytes;
    private ulong next;
    private bool sealedAdmission, failed;

    public BufferedStdioAdmission(int maxRecords = 1024, int maxBytes = 4194304,
        int maxWaiters = 64, int maxPendingBytes = 1048576)
    {
        if (maxRecords is < 1 or > 1024 || maxBytes is < 1 or > 4194304 ||
            maxWaiters is < 1 or > 64 || maxPendingBytes is < 1 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(maxRecords));
        long old = Volatile.Read(ref nextDescriptor);
        while (true)
        {
            if (old == long.MaxValue) throw new InvalidOperationException("descriptor identity exhausted");
            long observed = Interlocked.CompareExchange(ref nextDescriptor, old + 1, old);
            if (observed == old) { descriptor = old + 1; break; }
            old = observed;
        }
        recordsLimit = maxRecords; bytesLimit = maxBytes;
        waitLimit = maxWaiters; pendingLimit = maxPendingBytes;
    }
    public int AcceptedRecords => records.Count + (active.HasValue ? 1 : 0);
    public int AcceptedBytes => acceptedBytes;
    public int PendingRecords => waiting.Count;
    public int PendingBytes => pendingBytes;
    public int RetainedReceipts => ready.Count;
    public bool Drained => AcceptedRecords == 0;
    private bool Fits(int bytes) => AcceptedRecords < recordsLimit && bytes <= bytesLimit - acceptedBytes;
    private void Commit(ulong id, byte[] payload)
    {
        records.Enqueue(new(id, payload)); acceptedBytes += payload.Length;
    }
    private void Promote()
    {
        if (sealedAdmission || failed) return;
        while (waiting.Count != 0 && Fits(waiting[0].Payload.Length))
        {
            Waiter w = waiting[0]; waiting.RemoveAt(0);
            pendingBytes -= w.Payload.Length;
            Commit(w.Ticket.Id, w.Payload);
            ready.Add(new(w.Ticket, Result.Accepted));
        }
    }
    public Result Admit(Owner owner, string text, out Ticket ticket)
    {
        ticket = default;
        if (owner.Execution == 0 || owner.Generation == 0 || text is null) return Result.Invalid;
        if (failed) return Result.Failed;
        if (sealedAdmission) return Result.Closed;
        if (text.Length > 65536) return Result.Oversize;
        int bytes;
        try { bytes = StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException) { return Result.Invalid; }
        if (bytes > 65536 || bytes > bytesLimit) return Result.Oversize;
        bool immediate = waiting.Count == 0 && Fits(bytes);
        if (!immediate && (waiting.Count + ready.Count >= waitLimit || bytes > pendingLimit - pendingBytes))
            return Result.Quota;
        if (next == ulong.MaxValue) return Result.Quota;
        byte[] payload = StrictUtf8.GetBytes(text);
        ulong id = ++next;
        if (immediate) { Commit(id, payload); return Result.Accepted; }
        ticket = new(descriptor, id, owner);
        waiting.Add(new(ticket, payload)); pendingBytes += bytes;
        return Result.Pending;
    }
    public Result TakeReady(Owner owner, Ticket ticket)
    {
        if (ticket.Descriptor != descriptor || ticket.Owner != owner) return Result.Stale;
        int index = ready.FindIndex(r => r.Ticket == ticket);
        if (index >= 0)
        {
            Result outcome = ready[index].Outcome; ready.RemoveAt(index); return outcome;
        }
        return waiting.Exists(w => w.Ticket == ticket) ? Result.Pending : Result.Stale;
    }
    public bool Cancel(Owner owner, Ticket ticket)
    {
        if (ticket.Descriptor != descriptor || ticket.Owner != owner) return false;
        int index = waiting.FindIndex(w => w.Ticket == ticket);
        if (index < 0) return false;
        pendingBytes -= waiting[index].Payload.Length; waiting.RemoveAt(index); Promote();
        return true;
    }
    public void Retire(Owner owner)
    {
        for (int i = waiting.Count - 1; i >= 0; --i)
            if (waiting[i].Ticket.Owner == owner)
            { pendingBytes -= waiting[i].Payload.Length; waiting.RemoveAt(i); }
        ready.RemoveAll(r => r.Ticket.Owner == owner);
        Promote();
    }
    // Returned array is transferred to the trusted sink, never exposed to guest.
    public bool TakeSink(out SinkTicket ticket, out byte[]? payload)
    {
        ticket = default; payload = null;
        if (active.HasValue || failed || records.Count == 0) return false;
        Record r = records.Dequeue(); ticket = new(descriptor, r.Id);
        active = (ticket, r.Payload.Length); payload = r.Payload; return true;
    }
    public Result FinishSink(SinkTicket ticket, bool success)
    {
        if (active is not { } a || a.Ticket != ticket) return Result.Stale;
        active = null; acceptedBytes -= a.Bytes;
        if (!success) { Abort(); return Result.Failed; }
        if (failed) return Result.Failed;
        Promote(); return Result.Accepted;
    }
    public void Seal()
    {
        sealedAdmission = true;
        foreach (Waiter w in waiting) ready.Add(new(w.Ticket, Result.Closed));
        waiting.Clear(); pendingBytes = 0;
    }
    public void Abort()
    {
        if (failed) return;
        failed = true; sealedAdmission = true;
        foreach (Waiter w in waiting) ready.Add(new(w.Ticket, Result.Failed));
        waiting.Clear(); pendingBytes = 0; records.Clear();
        acceptedBytes = active?.Bytes ?? 0; // retain physically in-flight charge
    }
}
