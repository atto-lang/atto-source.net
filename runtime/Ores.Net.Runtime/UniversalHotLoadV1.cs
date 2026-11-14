namespace Ores.Net.Runtime;

/// <summary>
/// UHL-1 trusted-host capability discovery for the independent NativeAOT backend.
/// Not a dynamically loaded managed assembly ABI or an executable native export.
///
/// The current shipping frontend produces statically linked AOT code and has
/// no source actor/ETS hot migration. Refuse all transactional operations.
/// </summary>
public static class UniversalHotLoadV1
{
    public const uint AbiVersion = 1;
    public const string InterfaceName = "ores.hotload/v1";

    public enum Status : uint
    {
        Ok = 0,
        Unsupported = 1,
        Denied = 2,
        BadAbi = 3,
        StaleEpoch = 4,
        Busy = 5,
        ResourceLimit = 6,
        MigrationFailed = 7,
        Quarantined = 8,
        BadHandle = 9,
        Internal = 10
    }

    [Flags]
    public enum Features : ulong
    {
        None = 0,
        CodeOnly = 1UL << 0,
        StateMigration = 1UL << 1,
        SupervisedSubtree = 1UL << 2,
        EtsHeir = 1UL << 3,
        EtsMigration = 1UL << 4,
        Untrusted = 1UL << 5,
        NativeUnload = 1UL << 6,
        AotToAot = 1UL << 16,
        AotToJit = 1UL << 17,
        JitToAot = 1UL << 18,
        JitToJit = 1UL << 19
    }

    public readonly record struct Capabilities(
        uint AbiVersion,
        Features Supported,
        ulong MaxManifestBytes,
        ulong MaxCheckpointBytes,
        ulong MaxLiveGenerations);

    /// <summary>
    /// Reports no native actor/ETS hot-load support. A statically linked
    /// NativeAOT image is not a dynamically loadable assembly.
    /// </summary>
    public static Status QueryCapabilities(uint requestedAbi, out Capabilities capabilities)
    {
        capabilities = default;
        if (requestedAbi != AbiVersion) return Status.BadAbi;
        capabilities = new Capabilities(AbiVersion, Features.None, 0, 0, 0);
        return Status.Ok;
    }

    /// <summary>Never inspects or executes user-provided manifests.</summary>
    public static Status Stage(ReadOnlySpan<byte> authenticatedManifest)
    {
        _ = authenticatedManifest;
        return Status.Unsupported;
    }

    /// <summary>Do not mutate supervision state or increment route epochs.</summary>
    public static Status Prepare(ulong transactionId) => Status.Unsupported;
    public static Status Freeze(ulong transactionId, ulong expectedEpoch) => Status.Unsupported;
    public static Status Quiesce(ulong transactionId) => Status.Unsupported;
    public static Status Commit(ulong transactionId, ulong expectedEpoch) => Status.Unsupported;
    public static Status Abort(ulong transactionId) => Status.Unsupported;
    public static Status Retire(ulong generationId) => Status.Unsupported;

    /// <summary>
    /// An actor-owned table requires a runtime-owned authenticated heir.
    /// No ETS substrate may be created through this experimental ABI.
    /// </summary>
    public static Status AcknowledgeEtsHeir(
        ulong transactionId, ulong receiptId, ulong heirId) => Status.Unsupported;
}
