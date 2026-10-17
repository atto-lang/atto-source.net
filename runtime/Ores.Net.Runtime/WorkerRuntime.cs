using System.Globalization;

namespace Ores.Net.Runtime;

/// <summary>
/// Stdio worker loop for the statically linked NativeAOT scalar bootstrap.
///
/// This is a real process protocol, but not yet the full Lambda ABI: the current
/// .NET compiler exposes main() -> i64 only, so Invoke payloads must be empty.
/// Deadlines are checked before guest entry. Mid-call cancellation requires
/// future cooperative suspension support; supervisors must terminate the worker
/// process tree when a running synchronous call exceeds its deadline.
/// </summary>
public static class WorkerRuntime
{
    public const int ProtocolFailureExitCode = 74;

    public static int Run(
        Func<long> entry,
        Stream input,
        Stream output,
        TextWriter error,
        Func<long>? unixMilliseconds = null,
        WorkerRuntimeSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        unixMilliseconds ??= () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        settings ??= WorkerRuntimeSettings.StrictFaas;
        settings.Validate();

        try
        {
            WorkerProtocol.WriteFrame(output, WorkerFrame.Ready());
            ulong lastInvokeRequestId = 0;
            while (true)
            {
                var frame = WorkerProtocol.ReadFrame(input);
                if (frame is null) return 0;

                switch (frame.Kind)
                {
                    case WorkerFrameKind.Shutdown:
                        return 0;

                    case WorkerFrameKind.Cancel:
                        // The bootstrap entry is synchronous and has no
                        // cooperation points yet. Never claim in-process
                        // cancellation that cannot actually interrupt it.
                        WorkerProtocol.WriteFrame(
                            output,
                            WorkerFrame.Error(
                                frame.RequestId,
                                "cancel-requires-cooperative-guest"));
                        break;

                    case WorkerFrameKind.Invoke:
                        if (frame.RequestId <= lastInvokeRequestId)
                        {
                            WorkerProtocol.WriteFrame(
                                output,
                                WorkerFrame.Error(
                                    frame.RequestId,
                                    "request-id-replay"));
                            break;
                        }
                        lastInvokeRequestId = frame.RequestId;
                        if (!HandleInvoke(
                                entry,
                                output,
                                error,
                                frame,
                                unixMilliseconds(),
                                settings.MaxInvocationMilliseconds,
                                settings.MaxGuestOutputBytes))
                            return 70;
                        break;

                    default:
                        error.WriteLine("oreslang worker: host sent invalid frame kind");
                        return ProtocolFailureExitCode;
                }
            }
        }
        catch (Exception ex) when (
            ex is FormatException or EndOfStreamException or IOException)
        {
            error.WriteLine("oreslang worker: protocol failure");
            return ProtocolFailureExitCode;
        }
    }

    private static bool HandleInvoke(
        Func<long> entry,
        Stream output,
        TextWriter guestLog,
        WorkerFrame frame,
        long nowUnixMilliseconds,
        int maxInvocationMilliseconds,
        int maxGuestOutputBytes)
    {
        var deadlineError = WorkerProtocol.ValidateDeadline(
            frame.DeadlineUnixMilliseconds,
            nowUnixMilliseconds,
            maxInvocationMilliseconds);
        if (deadlineError is not null)
        {
            WorkerProtocol.WriteFrame(
                output, WorkerFrame.Error(frame.RequestId, deadlineError));
            return true;
        }

        // Current .NET bootstrap supports main() -> i64 only. A payload-bearing
        // invocation would otherwise pretend String -> String Lambda parity.
        if (frame.Payload.Length != 0)
        {
            WorkerProtocol.WriteFrame(
                output,
                WorkerFrame.Error(
                    frame.RequestId,
                    "payload-unsupported-by-scalar-bootstrap"));
            return true;
        }

        try
        {
            var value = ExecuteGuestBounded(
                entry,
                guestLog,
                maxGuestOutputBytes);
            WorkerProtocol.WriteFrame(
                output,
                WorkerFrame.Response(
                    frame.RequestId,
                    value.ToString(CultureInfo.InvariantCulture)));
            return true;
        }
        catch (GuestOutputLimitException)
        {
            WorkerProtocol.WriteFrame(
                output, WorkerFrame.Error(frame.RequestId, "guest-output-limit"));
            // A guest that exceeded its log budget is retired so no state from
            // a potentially runaway invocation is reused.
            return false;
        }
        catch (OverflowException)
        {
            WorkerProtocol.WriteFrame(
                output, WorkerFrame.Error(frame.RequestId, "arithmetic-overflow"));
            return true;
        }
        catch (DivideByZeroException)
        {
            WorkerProtocol.WriteFrame(
                output, WorkerFrame.Error(frame.RequestId, "division-by-zero"));
            return true;
        }
        catch (Exception)
        {
            // Do not reflect arbitrary exception messages or stack traces over
            // the invocation channel.
            WorkerProtocol.WriteFrame(
                output, WorkerFrame.Error(frame.RequestId, "guest-failure"));
            // Unknown failures may have left guest/runtime state inconsistent.
            // Retire this process generation after emitting the opaque result.
            return false;
        }
    }
    private static long ExecuteGuestBounded(
        Func<long> entry,
        TextWriter guestLog,
        int maxGuestOutputBytes)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var bounded = new BoundedGuestTextWriter(
            guestLog,
            maxGuestOutputBytes);
        try
        {
            // ORW1 owns stdout exclusively. Guest Console.Out and Console.Error
            // are redirected to the bounded log channel for the duration of
            // guest execution and restored before any protocol frame is written.
            Console.SetOut(bounded);
            Console.SetError(bounded);
            return entry();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

}
