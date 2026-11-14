using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Ores.Net.Runtime.Http;

/// <summary>Single-use, destination-bound authority. This is not wire data.</summary>
public sealed class HttpExchangeTicket
{
    internal HttpExchangeTicket(HttpExchangeLease lease, ulong generation, ActorRef owner)
    { Lease = lease; Generation = generation; Owner = owner; }
    internal HttpExchangeLease Lease { get; }
    internal ulong Generation { get; }
    internal ActorRef Owner { get; }
    internal bool Consumed { get; set; }
}

/// <summary>
/// Actor-visible authority over one exchange. The connection and HTTP framing
/// remain transport-owned. Every operation requires a live physical actor turn.
/// </summary>
public sealed class ActorHttpExchange
{
    private readonly HttpExchangeLease _lease;
    private readonly ulong _generation;
    internal ActorHttpExchange(HttpExchangeLease lease, ulong generation)
    { _lease = lease; _generation = generation; }

    public static ActorHttpExchange Claim(ActorContext actor, HttpExchangeTicket ticket) =>
        ticket.Lease.Claim(actor, ticket);

    public string Method(ActorContext actor) => _lease.Read(actor, _generation, c => c.Request.Method);
    public string Path(ActorContext actor) => _lease.Read(actor, _generation, c => c.Request.Path.Value ?? "/");
    public string Header(ActorContext actor, string name) =>
        _lease.Read(actor, _generation, c => c.Request.Headers[name].ToString());
    public int BodyLength(ActorContext actor) => _lease.BodyLength(actor, _generation);
    public void CopyBody(ActorContext actor, Span<byte> destination) =>
        _lease.CopyBody(actor, _generation, destination);
    public HttpExchangeTicket Move(ActorContext actor, ActorRef destination) =>
        _lease.Move(actor, _generation, destination);

    /// <summary>Starts native async response I/O; never blocks the actor carrier.</summary>
    public void Respond(ActorContext actor, int status, string contentType, string body) =>
        _lease.Respond(actor, _generation, status, contentType, body);
}

internal sealed class HttpExchangeLease
{
    private readonly object _gate = new();
    private HttpContext? _http;
    private readonly ActorRuntime _runtime;
    private readonly CancellationToken _cancellation;
    private readonly int _maxResponseBytes;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ActorRef _owner;
    private ulong _generation = 1;
    private bool _claimed;
    private bool _pending;
    private bool _closed;
    private Task? _physicalOperation;
    private byte[] _transitBody;
    private ActorArenaHandle? _body;

    internal HttpExchangeLease(ActorRuntime runtime, HttpContext http, byte[] body, int maxResponseBytes,
        CancellationToken cancellation)
    { _runtime = runtime; _http = http; _transitBody = body; _maxResponseBytes = maxResponseBytes; _cancellation = cancellation; }
    internal Task Completion => _completion.Task;
    internal Task OwnerTermination { get; private set; } = Task.CompletedTask;
    internal void Fail(Exception failure) => _completion.TrySetException(failure);
    internal Task StopOwner()
    {
        lock (_gate)
        {
            if (_owner.Id != 0)
            {
                try { _runtime.Stop(_owner); }
                catch (InvalidOperationException) { }
            }
            return OwnerTermination;
        }
    }

    internal HttpExchangeTicket Admit(ActorRef destination)
    {
        lock (_gate)
        {
            if (_owner.Id != 0 || destination.Id == 0) throw new InvalidOperationException("Exchange already admitted or invalid actor");
            OwnerTermination = _runtime.ObserveTermination(destination);
            _owner = destination;
            return new HttpExchangeTicket(this, _generation, destination);
        }
    }

    internal ActorHttpExchange Claim(ActorContext actor, HttpExchangeTicket ticket)
    {
        actor.ValidateActiveTurn();
        lock (_gate)
        {
            if (_closed || !ReferenceEquals(ticket.Lease, this) || ticket.Consumed ||
                _claimed || ticket.Generation != _generation || ticket.Owner != actor.Self || _owner != actor.Self)
                throw new InvalidOperationException("Stale, foreign or consumed HTTP transfer");
            // Publish claim only after actor-owned allocation succeeds.
            if (_transitBody.Length != 0)
            {
                _body = actor.AllocatePersistent(_transitBody.Length);
                actor.Write(_body, 0, _transitBody);
                CryptographicOperations.ZeroMemory(_transitBody);
                _transitBody = [];
            }
            ticket.Consumed = true;
            _claimed = true;
            return new ActorHttpExchange(this, _generation);
        }
    }

    private void RequireOwner(ActorContext actor, ulong generation)
    {
        actor.ValidateActiveTurn();
        if (_closed || !_claimed || _generation != generation || _owner != actor.Self)
            throw new InvalidOperationException("Stale or foreign HTTP exchange owner");
        _cancellation.ThrowIfCancellationRequested();
    }

    internal T Read<T>(ActorContext actor, ulong generation, Func<HttpContext, T> read)
    {
        lock (_gate) { RequireOwner(actor, generation); return read(_http!); }
    }

    internal int BodyLength(ActorContext actor, ulong generation)
    {
        lock (_gate) { RequireOwner(actor, generation); return _body?.Length ?? 0; }
    }

    internal void CopyBody(ActorContext actor, ulong generation, Span<byte> destination)
    {
        lock (_gate)
        {
            RequireOwner(actor, generation);
            if (destination.Length != (_body?.Length ?? 0)) throw new ArgumentException("Body destination length mismatch");
            if (_body is not null) actor.Read(_body, 0, destination);
        }
    }

    internal HttpExchangeTicket Move(ActorContext actor, ulong generation, ActorRef destination)
    {
        lock (_gate)
        {
            RequireOwner(actor, generation);
            if (_pending || destination.Id == 0 || destination == actor.Self || _generation == ulong.MaxValue)
                throw new InvalidOperationException("HTTP transfer not admissible");
            Task targetTermination = _runtime.ObserveTermination(destination);
            if (_body is not null)
            {
                byte[] transit = new byte[_body.Length];
                actor.Read(_body, 0, transit);
                actor.ReleasePersistent(_body);
                _body = null;
                _transitBody = transit;
            }
            _claimed = false;
            _owner = destination;
            OwnerTermination = targetTermination;
            _generation++;
            return new HttpExchangeTicket(this, _generation, destination);
        }
    }

    internal void Respond(ActorContext actor, ulong generation, int status, string contentType, string body)
    {
        lock (_gate)
        {
            RequireOwner(actor, generation);
            if (_pending) throw new InvalidOperationException("HTTP response already started");
            if (status is < 200 or > 599 || string.IsNullOrWhiteSpace(contentType) ||
                contentType.Contains('\r') || contentType.Contains('\n'))
                throw new ArgumentException("Invalid HTTP response metadata");
            if (Encoding.UTF8.GetByteCount(body) > _maxResponseBytes)
                throw new ArgumentOutOfRangeException(nameof(body), "HTTP response body limit exceeded");
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            var pin = actor.PinLifetime();
            _pending = true;
            _physicalOperation = WriteResponseAsync(status, contentType, bytes, pin);
        }
    }

    private async Task WriteResponseAsync(int status, string contentType, byte[] bytes, ActorArenaLifetimePin pin)
    {
        try
        {
            var http = _http!;
            http.Response.StatusCode = status;
            http.Response.ContentType = contentType;
            http.Response.ContentLength = status is 204 or 304 ? null : bytes.Length;
            if (http.Request.Method != "HEAD" && status is not (204 or 304))
                await http.Response.Body.WriteAsync(bytes, _cancellation).ConfigureAwait(false);
            await http.Response.CompleteAsync().ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (Exception failure) { _completion.TrySetException(failure); }
        finally { CryptographicOperations.ZeroMemory(bytes); pin.Dispose(); }
    }

    internal async Task CloseAsync()
    {
        Task physical;
        lock (_gate)
        {
            _closed = true;
            CryptographicOperations.ZeroMemory(_transitBody);
            _transitBody = [];
            physical = _physicalOperation ?? Task.CompletedTask;
        }
        await physical.ConfigureAwait(false);
        lock (_gate) _http = null;
    }
}
