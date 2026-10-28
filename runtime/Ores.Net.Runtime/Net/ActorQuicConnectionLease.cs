using System.Net.Quic;
using System.Runtime.Versioning;
using System.Security;

namespace Ores.Net.Runtime.Net;

/// <summary>
/// Opaque actor-owned connection capability. Raw QuicConnection and QuicStream
/// are never handed to guests. Stopping the actor revokes all operations and
/// retires the transport; DisposeAsync is the physical cleanup receipt.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("osx")]
internal sealed class ActorQuicConnectionLease : IAsyncDisposable
{
    private readonly ActorQuicOperation _operation;
    private readonly ActorQuicConnectionResource _resource;

    internal ActorQuicConnectionLease(
        ActorQuicOperation operation, ActorQuicConnectionResource resource)
    {
        _operation = operation;
        _resource = resource;
    }

    internal Task Retired => _operation.Retired;

    internal ValueTask<ActorQuicStreamLease> OpenOutboundStreamAsync(
        QuicStreamType type, CancellationToken cancellationToken)
        => _resource.OpenOutboundStreamAsync(type, cancellationToken);

    public ValueTask DisposeAsync() => _operation.DisposeAsync();
}

/// <summary>
/// Resource owned by an ActorQuicOperation, never by a caller. Tracks
/// in-flight stream opens, all admitted streams and physical teardown.
/// Stop cannot report completion while OpenOutboundStreamAsync can still
/// return an untracked native stream.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("osx")]
internal sealed class ActorQuicConnectionResource : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly QuicConnection _connection;
    private readonly ActorQuicOperation _operation;
    private readonly HashSet<ActorQuicStreamLease> _streams = new();
    private readonly TaskCompletionSource<bool> _opensDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const int MaxStreams = 32;
    private int _opening;
    private bool _closing;

    internal ActorQuicConnectionResource(
        QuicConnection connection, ActorQuicOperation operation)
    {
        _connection = connection;
        _operation = operation;
    }

    internal bool IsStopping
    {
        get { lock (_gate) return _closing || _operation.IsStopping; }
    }

    internal CancellationTokenSource LinkedStop(
        CancellationToken cancellationToken, CancellationToken streamClose)
    {
        if (!cancellationToken.CanBeCanceled)
            throw new ArgumentException("QUIC stream needs bounded cancellation",
                                        nameof(cancellationToken));
        return CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _operation.StopToken, streamClose);
    }

    internal async ValueTask<ActorQuicStreamLease> OpenOutboundStreamAsync(
        QuicStreamType type, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            throw new ArgumentException("QUIC stream requires a cancellable token",
                                        nameof(cancellationToken));
        lock (_gate)
        {
            if (_closing || _operation.IsStopping)
                throw new SecurityException("Actor QUIC stream authority retired");
            if (_streams.Count + _opening >= MaxStreams)
                throw new InvalidOperationException("Actor QUIC stream quota exceeded");
            ++_opening;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _operation.StopToken);
            var stream = await _connection.OpenOutboundStreamAsync(
                type, linked.Token).ConfigureAwait(false);
            ActorQuicStreamLease? lease = null;
            bool admitted;
            try
            {
                lease = new ActorQuicStreamLease(this, stream);
                lock (_gate)
                {
                    admitted = !_closing && !_operation.IsStopping &&
                        !linked.IsCancellationRequested;
                    if (admitted) _streams.Add(lease);
                }
                if (!admitted)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    throw new OperationCanceledException(
                        "Actor QUIC stream publication revoked");
                }
                return lease;
            }
            catch
            {
                if (lease is null)
                    await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lock (_gate)
            {
                --_opening;
                if (_closing && _opening == 0)
                    _opensDrained.TrySetResult(true);
            }
        }
    }

    internal void Unregister(ActorQuicStreamLease stream)
    {
        lock (_gate) _streams.Remove(stream);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closing)
                throw new InvalidOperationException(
                    "Native QUIC transport disposal invoked twice");
            _closing = true;
            if (_opening == 0) _opensDrained.TrySetResult(true);
        }
        // Wait for every pending open to settle BEFORE copying the set.
        await _opensDrained.Task.ConfigureAwait(false);
        ActorQuicStreamLease[] streams;
        lock (_gate) streams = _streams.ToArray();
        Exception? failure = null;
        foreach (var stream in streams)
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null)
            throw new InvalidOperationException(
                "QUIC native retirement incomplete", failure);
    }
}

/// <summary>
/// Narrow stream capability; no raw stream leaks. Each operation checks
/// revocation and joins the caller token with the actor stop token.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("osx")]
internal sealed class ActorQuicStreamLease : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ActorQuicConnectionResource _owner;
    private readonly QuicStream _stream;
    private readonly CancellationTokenSource _close = new();
    private readonly TaskCompletionSource<bool> _ioDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposal;
    private int _inFlight;
    private bool _closing;

    internal ActorQuicStreamLease(
        ActorQuicConnectionResource owner, QuicStream stream)
    {
        _owner = owner;
        _stream = stream;
    }

    private void BeginIo()
    {
        lock (_gate)
        {
            if (_closing || _owner.IsStopping)
                throw new SecurityException("Actor QUIC stream retired");
            // Prevent unbounded in-flight callbacks per stream.
            if (_inFlight >= 16)
                throw new InvalidOperationException("Actor QUIC stream I/O quota exceeded");
            ++_inFlight;
        }
    }

    private void EndIo()
    {
        lock (_gate)
        {
            --_inFlight;
            if (_closing && _inFlight == 0) _ioDrained.TrySetResult(true);
        }
    }

    internal Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return _disposal;
            _closing = true;
            if (_inFlight == 0) _ioDrained.TrySetResult(true);
            return _disposal = DisposePhysicalAsync();
        }
    }

    private async Task DisposePhysicalAsync()
    {
        Exception? failure = null;
        try
        {
            // Even an exceptional cancellation callback MUST NOT skip
            // physical native stream disposal. Preserve the failure.
            try { await _close.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            await _ioDrained.Task.ConfigureAwait(false);
            try { await _stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        finally
        {
            _close.Dispose();
            _owner.Unregister(this);
        }
        if (failure is not null)
            throw new InvalidOperationException(
                "Native QUIC stream retirement incomplete", failure);
    }

    internal async ValueTask<int> ReadAsync(
        Memory<byte> destination, CancellationToken cancellationToken)
    {
        BeginIo();
        try
        {
            using var linked = _owner.LinkedStop(cancellationToken, _close.Token);
            return await _stream.ReadAsync(destination, linked.Token)
                .ConfigureAwait(false);
        }
        finally { EndIo(); }
    }

    internal async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        BeginIo();
        try
        {
            using var linked = _owner.LinkedStop(cancellationToken, _close.Token);
            await _stream.WriteAsync(source, linked.Token).ConfigureAwait(false);
        }
        finally { EndIo(); }
    }

    public ValueTask DisposeAsync() => new(DisposeCoreAsync());
}
