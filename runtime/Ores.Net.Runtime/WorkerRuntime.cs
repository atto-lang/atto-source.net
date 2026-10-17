using System.Globalization;
using System.Text;

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
        return RunCore(
            input,
            output,
            error,
            unixMilliseconds,
            settings,
            (frame, now, admittedSettings) => HandleInvoke(
                entry,
                output,
                error,
                frame,
                now,
                admittedSettings.MaxInvocationMilliseconds,
                admittedSettings.MaxGuestOutputBytes));
    }

    /// <summary>
    /// Candidate provider-neutral text ABI for the runtime layer.
    ///
    /// This overload does not imply compiler support. The NativeAOT host remains
    /// wired to the scalar Run(Func&lt;long&gt;, ...) entry until generated CIL
    /// exposes a statically linked String -> String method.
    /// </summary>
    public static int RunText(
        Func<string, string> entry,
        Stream input,
        Stream output,
        TextWriter error,
        Func<long>? unixMilliseconds = null,
        WorkerRuntimeSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RunCore(
            input,
            output,
            error,
            unixMilliseconds,
            settings,
            (frame, now, admittedSettings) => HandleTextInvoke(
                entry,
                output,
                error,
                frame,
                now,
                admittedSettings.MaxInvocationMilliseconds,
                admittedSettings.MaxGuestOutputBytes));
    }

    private static int RunCore(
        Stream input,
        Stream output,
        TextWriter error,
        Func<long>? unixMilliseconds,
        WorkerRuntimeSettings? settings,
        Func<WorkerFrame, long, WorkerRuntimeSettings, bool> handleInvoke)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(handleInvoke);
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
                        if (!handleInvoke(
                                frame,
                                unixMilliseconds(),
                                settings))
                            return 70;
                        break;

                    default:
                        error.WriteLine(
                            "oreslang worker: host sent invalid frame kind");
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
    private static bool HandleTextInvoke(
        Func<string, string> entry,
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
                output,
                WorkerFrame.Error(frame.RequestId, deadlineError));
            return true;
        }

        try
        {
            var value = ExecuteGuestBounded(
                () => entry(frame.Payload),
                guestLog,
                maxGuestOutputBytes);
            if (!TryValidateTextResponse(value, out var responseError))
            {
                WorkerProtocol.WriteFrame(
                    output,
                    WorkerFrame.Error(frame.RequestId, responseError));
                return false;
            }
            WorkerProtocol.WriteFrame(
                output,
                WorkerFrame.Response(frame.RequestId, value));
            return true;
        }
        catch (GuestOutputLimitException)
        {
            WorkerProtocol.WriteFrame(
                output,
                WorkerFrame.Error(frame.RequestId, "guest-output-limit"));
            return false;
        }
        catch (Exception)
        {
            WorkerProtocol.WriteFrame(
                output,
                WorkerFrame.Error(frame.RequestId, "guest-failure"));
            return false;
        }
    }

    private static bool TryValidateTextResponse(
        string? value,
        out string errorCode)
    {
        if (value is null)
        {
            errorCode = "guest-null-response";
            return false;
        }

        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) >
                WorkerProtocol.MaxPayloadBytes)
            {
                errorCode = "response-too-large";
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            errorCode = "response-invalid-utf8";
            return false;
        }

        errorCode = "";
        return true;
    }

    private static T ExecuteGuestBounded<T>(
        Func<T> entry,
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
