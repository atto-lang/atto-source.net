using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ores.Net.Runtime.Http;

/// <summary>Compiler-bound handler slot; guest closures cannot populate this API.</summary>
public sealed record ActorHttpEndpoint(ActorKind Kind, Action<ActorContext, ActorHttpExchange> Handle);

public sealed class ActorHttpServerOptions
{
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public int Port { get; init; }
    public int MaxRequests { get; init; } = 128;
    public int MaxBodyBytes { get; init; } = 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (Port is < 0 or > 65535 || MaxRequests is < 1 or > 65536 ||
            MaxBodyBytes is < 0 or > 16 * 1024 * 1024 ||
            MaxResponseBytes is < 0 or > 16 * 1024 * 1024 ||
            RequestTimeout < TimeSpan.FromMilliseconds(1) || RequestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(ActorHttpServerOptions));
        ArgumentNullException.ThrowIfNull(Address);
    }
}

/// <summary>
/// Native-AOT-compatible asynchronous HTTP transport. Compiled Spin supplies
/// routing; this host provider does not replace the Oreslang source frontend.
/// </summary>
public sealed class ActorHttpServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ActorRuntime _actors;
    private readonly ActorHttpServerOptions _options;
    private readonly Func<string, string, ActorHttpEndpoint> _resolve;
    private readonly SemaphoreSlim _admission;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private readonly object _retirementGate = new();
    private readonly HashSet<Task> _retirements = [];

    private ActorHttpServer(ActorRuntime actors, ActorHttpServerOptions options,
        Func<string, string, ActorHttpEndpoint> resolve)
    {
        _actors = actors; _options = options; _resolve = resolve;
        _admission = new SemaphoreSlim(options.MaxRequests, options.MaxRequests);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(options.Address, options.Port, listen =>
                listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
            server.Limits.MaxRequestBodySize = options.MaxBodyBytes;
            server.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            server.Limits.MaxRequestLineSize = 8 * 1024;
            server.Limits.RequestHeadersTimeout = options.RequestTimeout;
        });
        _application = builder.Build();
        _application.Run(DispatchAsync);
    }

    public Uri Address { get; private set; } = null!;
    public int ActiveRequests => _options.MaxRequests - _admission.CurrentCount;

    public static async Task<ActorHttpServer> StartAsync(ActorRuntime actors,
        Func<string, string, ActorHttpEndpoint> resolve, ActorHttpServerOptions? options = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(resolve);
        options ??= new ActorHttpServerOptions(); options.Validate();
        var server = new ActorHttpServer(actors, options, resolve);
        try
        {
            await server._application.StartAsync(cancellation).ConfigureAwait(false);
            var addresses = server._application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!;
            server.Address = new Uri(addresses.Addresses.Single());
            return server;
        }
        catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task DispatchAsync(HttpContext http)
    {
        if (!await _admission.WaitAsync(0, http.RequestAborted).ConfigureAwait(false))
        { http.Response.StatusCode = 503; return; }
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _stopping.Token);
        timeout.CancelAfter(_options.RequestTimeout);
        HttpExchangeLease? lease = null; ActorRef actor = default; byte[]? body = null;
        Task rootTermination = Task.CompletedTask;
        try
        {
            body = await ReadBodyAsync(http, timeout.Token).ConfigureAwait(false);
            var endpoint = _resolve(http.Request.Method, http.Request.Path.Value ?? "/");
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(endpoint.Handle);
            if (endpoint.Kind is not (ActorKind.Private or ActorKind.Shared))
                throw new ArgumentException("HTTP route requires a shared or isolated actor");
            lease = new HttpExchangeLease(_actors, http, body, _options.MaxResponseBytes, timeout.Token);
            body = null;
            HttpExchangeTicket? ticket = null;
            actor = _actors.Spawn(endpoint.Kind, (context, _) =>
            {
                try { endpoint.Handle(context, ActorHttpExchange.Claim(context, ticket!)); }
                catch (Exception failure) { lease.Fail(failure); throw; }
            });
            ticket = lease.Admit(actor);
            rootTermination = lease.OwnerTermination;
            _actors.PostHostMessage(actor);
            await lease.Completion.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (BadHttpRequestException failure) when (failure.StatusCode == 413)
        { if (!http.Response.HasStarted) http.Response.StatusCode = 413; else http.Abort(); }
        catch (OperationCanceledException)
        { if (!http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested) http.Response.StatusCode = 504; else http.Abort(); }
        catch (Exception)
        { if (!http.Response.HasStarted) http.Response.StatusCode = 500; else http.Abort(); }
        finally
        {
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            if (lease is not null)
            {
                // HttpContext is released before this callback returns. Actor
                // retirement retains admission independently of keep-alive I/O.
                await lease.CloseAsync().ConfigureAwait(false);
                if (lease.Completion.IsFaulted) _ = lease.Completion.Exception;
            }
            Task retirement = RetireAsync(lease, actor, rootTermination, timeout);
            lock (_retirementGate) _retirements.Add(retirement);
            _ = ObserveRetirementAsync(retirement);
        }
    }

    private async Task RetireAsync(HttpExchangeLease? lease, ActorRef actor,
        Task rootTermination, CancellationTokenSource timeout)
    {
        try
        {
            if (lease is not null)
            {
                try { await lease.OwnerTermination.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                await lease.StopOwner().ConfigureAwait(false);
            }
        }
        finally
        {
            timeout.Cancel();
            if (actor.Id != 0) { try { _actors.Stop(actor); } catch (InvalidOperationException) { } }
            await rootTermination.ConfigureAwait(false);
            timeout.Dispose();
            _admission.Release();
        }
    }

    private async Task ObserveRetirementAsync(Task retirement)
    {
        try { await retirement.ConfigureAwait(false); }
        finally { lock (_retirementGate) _retirements.Remove(retirement); }
    }

    private async Task<byte[]> ReadBodyAsync(HttpContext http, CancellationToken cancellation)
    {
        if (http.Request.ContentLength > _options.MaxBodyBytes)
            throw new BadHttpRequestException("HTTP body limit exceeded", 413);
        byte[] buffer = new byte[_options.MaxBodyBytes + 1]; int count = 0;
        try
        {
            while (true)
            {
                int read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), cancellation).ConfigureAwait(false);
                count += read;
                if (count > _options.MaxBodyBytes) throw new BadHttpRequestException("HTTP body limit exceeded", 413);
                if (read == 0) return buffer.AsSpan(0, count).ToArray();
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stopGate) return new ValueTask(_stopTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        _stopping.Cancel();
        await _application.StopAsync().ConfigureAwait(false);
        Task[] retirements;
        lock (_retirementGate) retirements = _retirements.ToArray();
        await Task.WhenAll(retirements).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose(); _admission.Dispose();
    }
}
