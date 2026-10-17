using System.Globalization;

namespace Ores.Net.Runtime;

/// <summary>
/// Runtime limits passed from the supervising host to one NativeAOT worker.
/// Values may only tighten the ORW1 protocol ceilings; they can never widen
/// them beyond the reviewed protocol constants.
/// </summary>
public sealed record WorkerRuntimeSettings(
    int MaxInvocationMilliseconds,
    int MaxGuestOutputBytes)
{
    public static WorkerRuntimeSettings StrictFaas { get; } = new(
        WorkerProtocol.MaxInvocationMilliseconds,
        WorkerProtocol.MaxPayloadBytes);

    public void Validate()
    {
        if (MaxInvocationMilliseconds is < 1 or > WorkerProtocol.MaxInvocationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(MaxInvocationMilliseconds));
        if (MaxGuestOutputBytes is < 1 or > WorkerProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxGuestOutputBytes));
    }

    public static WorkerRuntimeSettings FromEnvironment(
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var protocol = getEnvironmentVariable("ORESLANG_WORKER_PROTOCOL");
        if (protocol is not null &&
            !string.Equals(protocol, WorkerProtocol.ReadyPayload, StringComparison.Ordinal))
            throw new FormatException("worker protocol environment identity mismatch");

        var wall = ParseBoundedPositive(
            getEnvironmentVariable("ORESLANG_WORKER_MAX_WALL_MS"),
            WorkerProtocol.MaxInvocationMilliseconds,
            WorkerProtocol.MaxInvocationMilliseconds,
            "ORESLANG_WORKER_MAX_WALL_MS");
        var output = ParseBoundedPositive(
            getEnvironmentVariable("ORESLANG_WORKER_MAX_OUTPUT_BYTES"),
            WorkerProtocol.MaxPayloadBytes,
            WorkerProtocol.MaxPayloadBytes,
            "ORESLANG_WORKER_MAX_OUTPUT_BYTES");

        var settings = new WorkerRuntimeSettings(wall, output);
        settings.Validate();
        return settings;
    }

    private static int ParseBoundedPositive(
        string? value,
        int defaultValue,
        int maximum,
        string name)
    {
        if (value is null)
            return defaultValue;
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed < 1 ||
            parsed > maximum)
            throw new FormatException($"{name} is outside the admitted worker limit");
        return parsed;
    }
}
