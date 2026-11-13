using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;

namespace Ores.Net.Runtime;

/// <summary>
/// Strict immutable W3C traceparent v00, not a guest capability. Rejects malformed
/// hex, zero trace/span ids and unsupported versions before allocating a scope.
/// </summary>
public readonly record struct NativeTraceParentV1(string TraceId, string SpanId, byte Flags)
{
    public static bool TryParse(string? raw, out NativeTraceParentV1 value)
    {
        value = default;
        if (raw is null || raw.Length != 55 || raw.AsSpan(0, 3).ToString() != "00-" ||
            raw[35] != '-' || raw[52] != '-') return false;
        if (!ValidHex(raw.AsSpan(3, 32)) || !ValidHex(raw.AsSpan(36, 16)) ||
            !ValidHex(raw.AsSpan(53, 2))) return false;
        string trace = raw.Substring(3, 32);
        string span = raw.Substring(36, 16);
        if (trace.AsSpan().IndexOfAnyExcept('0') < 0 ||
            span.AsSpan().IndexOfAnyExcept('0') < 0) return false;
        value = new NativeTraceParentV1(trace, span,
            (byte)((Hex(raw[53]) << 4) | Hex(raw[54])));
        return true;
    }

    private static bool ValidHex(ReadOnlySpan<char> value)
    {
        foreach (char ch in value)
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
        return true;
    }

    private static int Hex(char ch) => ch <= '9' ? ch - '0' : ch - 'a' + 10;

    public override string ToString() => $"00-{TraceId}-{SpanId}-{Flags:x2}";
}

/// <summary>
/// Trusted-host-only tracing substrate for NativeAOT and explicit scheduler
/// continuation handoff. This does not implement the AttoLang 'using' source
/// construct, CIL probes, an OTLP exporter, or actor scheduler authentication.
///
/// A Scope is minted only from a tracer-owned Owner, never from a guest value.
/// Immutable trace context can cross carriers; mutable scope authority cannot.
/// No AsyncLocal, reflection, dynamic code, clock, guest callbacks or I/O.
/// </summary>
public sealed class NativeTraceContinuationV1
{
    public enum Outcome { Ok, Error, Cancelled }

    public sealed class Owner
    {
        internal Owner(NativeTraceContinuationV1 tracer, ulong generation)
        { Tracer = tracer; Generation = generation; }
        internal NativeTraceContinuationV1 Tracer { get; }
        public ulong Generation { get; }
        internal int Revoked;
    }

    public sealed class Scope
    {
        internal Scope(Owner owner, int siteId, NativeTraceParentV1 trace,
            NativeTraceParentV1? parent)
        { Owner = owner; SiteId = siteId; Context = trace; Parent = parent; }
        internal Owner Owner { get; }
        internal int State; // 0 running; 1 suspended; 2 terminal
        public int SiteId { get; }
        public NativeTraceParentV1 Context { get; }
        public NativeTraceParentV1? Parent { get; }
    }

    public readonly record struct Receipt(int SiteId, NativeTraceParentV1 Context,
        NativeTraceParentV1? Parent, Outcome Result);

    private readonly bool enabled;
    private readonly int activeLimit;
    private readonly int receiptLimit;
    private readonly ConcurrentDictionary<Scope, byte> scopes = new();
    private readonly ConcurrentQueue<Receipt> receipts = new();
    private int active;
    private int reservedReceipts;
    private long dropped;
    private long completed;

    public NativeTraceContinuationV1(bool enabled = false, int maxActive = 64,
        int maxReceipts = 1024)
    {
        if (maxActive is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maxActive));
        if (maxReceipts is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maxReceipts));
        this.enabled = enabled;
        activeLimit = maxActive;
        receiptLimit = maxReceipts;
    }

    public int Active => Volatile.Read(ref active);
    public long Completed => Interlocked.Read(ref completed);
    public long Dropped => Interlocked.Read(ref dropped);

    // Only a privileged runtime/supervisor should call this; a guest API must
    // NEVER expose Owner or a method that mints it.
    public Owner MintOwner(ulong generation)
    {
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
        return new Owner(this, generation);
    }

    private bool Owns(Owner owner, Scope scope, ulong generation)
        => ReferenceEquals(owner.Tracer, this) &&
           ReferenceEquals(scope.Owner, owner) &&
           owner.Generation == generation && generation != 0 &&
           Volatile.Read(ref owner.Revoked) == 0;

    private bool Reserve()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int now = Volatile.Read(ref active);
            if (now >= activeLimit) return false;
            if (Interlocked.CompareExchange(ref active, now + 1, now) == now)
                return true;
        }
        return false;
    }

    private static string RandomHex(int bytes)
    {
        Span<byte> value = stackalloc byte[16];
        if (bytes is not (8 or 16)) throw new ArgumentOutOfRangeException(nameof(bytes));
        string result;
        do
        {
            RandomNumberGenerator.Fill(value[..bytes]);
            result = Convert.ToHexStringLower(value[..bytes]);
        } while (result.AsSpan().IndexOfAnyExcept('0') < 0);
        return result;
    }

    public Scope? Begin(Owner owner, ulong generation, int siteId,
        NativeTraceParentV1? parent = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!enabled) return null; // No entropy, guard or receipt work.
        if (siteId <= 0) throw new ArgumentOutOfRangeException(nameof(siteId));
        if (!ReferenceEquals(owner.Tracer, this) ||
            owner.Generation != generation || generation == 0 ||
            Volatile.Read(ref owner.Revoked) != 0)
            throw new InvalidOperationException("stale or foreign trace owner");
        if (parent.HasValue &&
            (!NativeTraceParentV1.TryParse(parent.Value.ToString(), out _)))
            throw new FormatException("Invalid inherited trace context");
        if (!Reserve())
        {
            Interlocked.Increment(ref dropped);
            return null;
        }
        try
        {
            string traceId = parent?.TraceId ?? RandomHex(16);
            string spanId = RandomHex(8);
            while (spanId == parent?.SpanId) spanId = RandomHex(8);
            var context = new NativeTraceParentV1(traceId, spanId,
                parent?.Flags ?? 0);
            var scope = new Scope(owner, siteId, context, parent);
            if (!scopes.TryAdd(scope, 0))
                throw new InvalidOperationException("duplicate compiler scope");
            // If retirement wins between admission and publication, immediately
            // close instead of leaking the just-published generation.
            if (Volatile.Read(ref owner.Revoked) != 0)
                Close(scope, Outcome.Cancelled);
            return scope;
        }
        catch
        {
            Interlocked.Decrement(ref active);
            throw;
        }
    }

    public bool Suspend(Owner owner, Scope scope, ulong generation)
        => Owns(owner, scope, generation) &&
           Interlocked.CompareExchange(ref scope.State, 1, 0) == 0;

    public bool Resume(Owner owner, Scope scope, ulong generation)
        => Owns(owner, scope, generation) &&
           Interlocked.CompareExchange(ref scope.State, 0, 1) == 1;

    public bool Finish(Owner owner, Scope scope, ulong generation, Outcome result)
        => Owns(owner, scope, generation) && Close(scope, result);

    private bool Close(Scope scope, Outcome outcome)
    {
        for (;;)
        {
            int state = Volatile.Read(ref scope.State);
            if (state == 2) return false;
            if (Interlocked.CompareExchange(ref scope.State, 2, state) == state)
                break;
        }
        scopes.TryRemove(scope, out _);
        Interlocked.Decrement(ref active);
        Interlocked.Increment(ref completed);
        bool reserved = false;
        for (int i = 0; i < 8; i++)
        {
            int count = Volatile.Read(ref reservedReceipts);
            if (count >= receiptLimit) break;
            if (Interlocked.CompareExchange(ref reservedReceipts,
                    count + 1, count) == count) { reserved = true; break; }
        }
        if (reserved)
            receipts.Enqueue(new Receipt(scope.SiteId, scope.Context, scope.Parent, outcome));
        else
            Interlocked.Increment(ref dropped);
        return true;
    }

    public Receipt? PollReceipt()
    {
        if (!receipts.TryDequeue(out Receipt receipt)) return null;
        Interlocked.Decrement(ref reservedReceipts);
        return receipt;
    }

    /// <summary>
    /// Supervisor-controlled actor-generation retirement. Stale/suspended
    /// scopes are cancelled exactly once even if racing with normal finish.
    /// </summary>
    public void Retire(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!ReferenceEquals(owner.Tracer, this))
            throw new InvalidOperationException("foreign trace owner");
        if (Interlocked.Exchange(ref owner.Revoked, 1) != 0) return;
        foreach (Scope scope in scopes.Keys)
            if (ReferenceEquals(scope.Owner, owner))
                Close(scope, Outcome.Cancelled);
    }
}
