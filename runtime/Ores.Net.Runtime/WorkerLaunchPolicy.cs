using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace Ores.Net.Runtime;

public sealed record WorkerResourceLimits(
    int MaxWallTimeMilliseconds,
    long MaxMemoryBytes,
    int MaxProcesses,
    int MaxOutputBytes)
{
    public static WorkerResourceLimits StrictFaas { get; } = new(
        WorkerProtocol.MaxInvocationMilliseconds,
        128L * 1024 * 1024,
        1,
        WorkerProtocol.MaxPayloadBytes);

    public void Validate()
    {
        if (MaxWallTimeMilliseconds is < 1 or > WorkerProtocol.MaxInvocationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(MaxWallTimeMilliseconds));
        if (MaxMemoryBytes is < 16L * 1024 * 1024 or > 4L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxMemoryBytes));
        if (MaxProcesses is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxProcesses));
        if (MaxOutputBytes is < 1 or > WorkerProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputBytes));
    }
}

/// <summary>
/// Canonical Deno-style capability tokens requested for a worker generation.
/// Empty is deny-all. V1 records these requests but the generic .NET launcher
/// does not yet implement scoped filesystem/network/process enforcement.
/// </summary>
public sealed class WorkerCapabilities
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlyCollection<string> _grants;

    private WorkerCapabilities(string[] grants) =>
        _grants = Array.AsReadOnly((string[])grants.Clone());

    public static WorkerCapabilities DenyAll { get; } = new(Array.Empty<string>());

    public IReadOnlyList<string> Grants => _grants;
    public bool IsDenyAll => _grants.Count == 0;

    public static WorkerCapabilities Create(params string[] grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (string.IsNullOrEmpty(grant))
                throw new FormatException("worker capability cannot be empty");
            int byteCount;
            try
            {
                byteCount = StrictUtf8.GetByteCount(grant);
            }
            catch (EncoderFallbackException ex)
            {
                throw new FormatException(
                    "worker capability is not valid Unicode", ex);
            }
            if (byteCount > 512)
                throw new FormatException("worker capability exceeds 512 UTF-8 bytes");
            if (grant.Any(char.IsControl))
                throw new FormatException("worker capability contains control characters");
            if (!HasKnownPrefix(grant))
                throw new FormatException("unknown worker capability kind");
            if (grant.EndsWith(':'))
                throw new FormatException("worker capability value cannot be empty");
            if (!seen.Add(grant))
                throw new FormatException("duplicate worker capability");
        }
        var canonical = seen.Order(StringComparer.Ordinal).ToArray();
        return new WorkerCapabilities(canonical);
    }

    private static bool HasKnownPrefix(string grant) =>
        grant.StartsWith("read:", StringComparison.Ordinal) ||
        grant.StartsWith("write:", StringComparison.Ordinal) ||
        grant.StartsWith("net:", StringComparison.Ordinal) ||
        grant.StartsWith("env:", StringComparison.Ordinal) ||
        grant.StartsWith("run:", StringComparison.Ordinal);
}

public sealed record WorkerLaunchManifest(
    WorkloadTrust Trust,
    WorkerResourceLimits Limits,
    WorkerCapabilities Capabilities)
{
    public static WorkerLaunchManifest StrictFaas(WorkloadTrust trust) =>
        new(trust, WorkerResourceLimits.StrictFaas, WorkerCapabilities.DenyAll);

    /// <summary>
    /// Admission for the generic unsandboxed process launcher. Untrusted workers
    /// must use a platform host (for example Scintilla) that applies the OS
    /// sandbox before guest code can execute. Scoped capabilities remain
    /// fail-closed until their enforcement adapters exist.
    /// </summary>
    public void ValidateForGenericLauncher()
    {
        Limits.Validate();
        _ = IsolationPolicy.Admit(Trust, IsolationKind.WorkerProcess);

        if (!Capabilities.IsDenyAll)
            throw new NotSupportedException(
                "scoped worker capabilities require a verified host enforcement adapter");

        if (Trust == WorkloadTrust.Untrusted)
            throw new SecurityException(
                "untrusted NativeAOT worker requires an OS-sandbox launcher");
    }
}

public static class WorkerProcessStartInfoFactory
{
    /// <summary>
    /// Creates a sanitized stdio worker launch for trusted/semi-trusted use.
    /// Environment inheritance is removed. This method deliberately refuses
    /// untrusted workers; a race-free OS-sandbox launcher must own those.
    /// </summary>
    public static ProcessStartInfo Create(
        string executablePath,
        WorkerLaunchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.ValidateForGenericLauncher();

        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException(
                "worker executable path must be absolute", nameof(executablePath));
        if (executablePath.IndexOf('\0') >= 0)
            throw new ArgumentException(
                "worker executable path contains NUL", nameof(executablePath));

        var workingDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(workingDirectory))
            throw new ArgumentException(
                "worker executable path has no parent directory",
                nameof(executablePath));

        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--worker");
        start.Environment.Clear();
        start.Environment["ORESLANG_WORKER_PROTOCOL"] = WorkerProtocol.ReadyPayload;
        start.Environment["ORESLANG_WORKER_MAX_WALL_MS"] =
            manifest.Limits.MaxWallTimeMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["ORESLANG_WORKER_MAX_OUTPUT_BYTES"] =
            manifest.Limits.MaxOutputBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return start;
    }
}
