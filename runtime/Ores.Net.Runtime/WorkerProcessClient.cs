using System.Diagnostics;
using System.Text;

namespace Ores.Net.Runtime;

public sealed record WorkerInvocationResult(bool IsSuccess, string Payload)
{
    public static WorkerInvocationResult Response(string payload) =>
        new(true, payload);

    public static WorkerInvocationResult Error(string code) =>
        new(false, code);
}

/// <summary>
/// Serial ORW1 supervisor for a trusted/semi-trusted NativeAOT worker process.
///
/// This class does not create an OS sandbox. Untrusted manifests are rejected by
/// WorkerProcessStartInfoFactory before process creation. Because the current
/// scalar guest cannot cooperatively observe Cancel frames while executing, a
/// host timeout or caller cancellation retires and kills the entire process
/// tree instead of pretending the guest was interrupted in-process.
/// </summary>
public sealed class WorkerProcessClient : IAsyncDisposable
{
    public const int StartupTimeoutMilliseconds = 5_000;
    public const int ShutdownGraceMilliseconds = 1_000;

    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly WorkerLaunchManifest _manifest;
    private readonly SemaphoreSlim _invokeGate = new(1, 1);
    private readonly Task _stderrDrain;
    private ulong _lastRequestId;
    private bool _retired;
    private bool _disposed;
    private volatile bool _stderrLimitExceeded;

    private WorkerProcessClient(
        Process process,
        WorkerLaunchManifest manifest)
    {
        _process = process;
        _manifest = manifest;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        _stderrDrain = DrainStandardErrorAsync();
    }

    public int ProcessId => _process.Id;
    public bool IsRetired => _retired || _process.HasExited;

    public static async Task<WorkerProcessClient> StartAsync(
        string executablePath,
        WorkerLaunchManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var startInfo = WorkerProcessStartInfoFactory.Create(
            executablePath,
            manifest);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(
                "worker process did not start");
        }

        var client = new WorkerProcessClient(process, manifest);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            startup.CancelAfter(StartupTimeoutMilliseconds);
            var ready = await WorkerProtocol.ReadFrameAsync(
                client._output,
                startup.Token).ConfigureAwait(false);
            if (ready != WorkerFrame.Ready())
                throw new InvalidDataException(
                    "worker did not emit the exact ORW1 ready handshake");
            client.EnsureStderrWithinLimit();
            return client;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            client.RetireAndKillTree();
            process.Dispose();
            throw new TimeoutException(
                "worker ready handshake timed out");
        }
        catch
        {
            client.RetireAndKillTree();
            process.Dispose();
            throw;
        }
    }

    public async Task<WorkerInvocationResult> InvokeAsync(
        string payload,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (timeoutMilliseconds is < 1 ||
            timeoutMilliseconds > _manifest.Limits.MaxWallTimeMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        await _invokeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            EnsureStderrWithinLimit();

            if (_lastRequestId == ulong.MaxValue)
            {
                RetireAndKillTree();
                throw new OverflowException(
                    "worker request id space exhausted");
            }
            var requestId = ++_lastRequestId;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < 0 || now > long.MaxValue - timeoutMilliseconds)
            {
                RetireAndKillTree();
                throw new InvalidOperationException(
                    "host clock cannot represent worker deadline");
            }
            var deadline = now + timeoutMilliseconds;

            using var invocation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            invocation.CancelAfter(timeoutMilliseconds);
            try
            {
                await WorkerProtocol.WriteFrameAsync(
                    _input,
                    WorkerFrame.Invoke(requestId, deadline, payload),
                    invocation.Token).ConfigureAwait(false);

                var frame = await WorkerProtocol.ReadFrameAsync(
                    _output,
                    invocation.Token).ConfigureAwait(false);
                if (frame is null)
                    throw new EndOfStreamException(
                        "worker closed stdout before invocation response");
                if (frame.RequestId != requestId ||
                    (frame.Kind != WorkerFrameKind.Response &&
                     frame.Kind != WorkerFrameKind.Error))
                    throw new InvalidDataException(
                        "worker returned an unexpected ORW1 response");

                EnsureStderrWithinLimit();

                if (frame.Kind == WorkerFrameKind.Response)
                    return WorkerInvocationResult.Response(frame.Payload);

                if (frame.Payload is "guest-failure" or "guest-output-limit")
                    _retired = true;
                return WorkerInvocationResult.Error(frame.Payload);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                RetireAndKillTree();
                throw new TimeoutException(
                    "worker invocation exceeded its host deadline");
            }
            catch (OperationCanceledException)
            {
                RetireAndKillTree();
                throw;
            }
            catch (Exception ex) when (
                ex is IOException or EndOfStreamException or
                FormatException or InvalidDataException)
            {
                RetireAndKillTree();
                throw new InvalidDataException(
                    "worker invocation protocol failed closed",
                    ex);
            }
        }
        finally
        {
            _invokeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _invokeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_process.HasExited && !_retired)
            {
                try
                {
                    await WorkerProtocol.WriteFrameAsync(
                        _input,
                        WorkerFrame.Shutdown()).ConfigureAwait(false);
                    using var grace =
                        new CancellationTokenSource(
                            ShutdownGraceMilliseconds);
                    await _process.WaitForExitAsync(
                        grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RetireAndKillTree();
                }
                catch (IOException)
                {
                    RetireAndKillTree();
                }
            }
            else if (!_process.HasExited)
            {
                RetireAndKillTree();
            }

            try
            {
                await _stderrDrain.ConfigureAwait(false);
            }
            catch when (_process.HasExited)
            {
                // The process boundary is already gone. Stderr teardown races
                // cannot resurrect or reuse this generation.
            }
        }
        finally
        {
            _invokeGate.Release();
            _invokeGate.Dispose();
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        var reader = _process.StandardError;
        var buffer = new char[4096];
        var totalBytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return;

            var bytes = Encoding.UTF8.GetByteCount(
                buffer.AsSpan(0, read));
            if (bytes > _manifest.Limits.MaxOutputBytes - totalBytes)
            {
                _stderrLimitExceeded = true;
                RetireAndKillTree();
                return;
            }
            totalBytes += bytes;
            // Intentionally discard child stderr after accounting. Guest logs
            // must not become an unbounded retained secret-bearing buffer in
            // the supervisor.
        }
    }

    private void EnsureUsable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkerProcessClient));
        if (_retired || _process.HasExited)
            throw new InvalidOperationException(
                "worker generation is retired");
    }

    private void EnsureStderrWithinLimit()
    {
        if (_stderrLimitExceeded)
            throw new InvalidDataException(
                "worker stderr exceeded its admitted byte limit");
        if (_stderrDrain.IsFaulted)
            throw new InvalidDataException(
                "worker stderr drain failed",
                _stderrDrain.Exception);
    }

    private void RetireAndKillTree()
    {
        _retired = true;
        if (_process.HasExited) return;

        // No single-process fallback: the current synchronous guest cannot
        // cooperatively handle a Cancel frame while it is inside entry().
        // Kill(true) is therefore the generic supervisor's minimum teardown
        // primitive. Untrusted execution still requires the stronger platform
        // sandbox/job/cgroup adapter admitted elsewhere.
        _process.Kill(entireProcessTree: true);
    }
}
