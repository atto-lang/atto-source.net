// Trusted-host proxy ACK transition model. Not a socket, cryptographic
// authentication verifier, compiler-emitted guest API, or NativeAOT parity proof.
// Only runtime code that has verified an authenticated destination/provider
// receipt may call ApplyVerified. No untrusted .atto actor obtains this gate.
namespace Atto.Runtime.Proxy;

public readonly record struct ReceiptIdentityV1(
    uint ProtocolMajor, ulong Tenant, ulong CallerIncarnation,
    ulong TargetActor, ulong TargetIncarnation, ulong AssociationGeneration,
    ulong PlacementEpoch,
    ulong MessageId, ulong Attempt)
{
    public bool IsValid =>
        ProtocolMajor == 1 && Tenant != 0 && CallerIncarnation != 0 &&
        TargetActor != 0 && TargetIncarnation != 0 && AssociationGeneration != 0 &&
        PlacementEpoch != 0 &&
        MessageId != 0 && Attempt != 0;
}

public enum ReceiptEventV1
{
    Admitted, Started, Processed, DurableInbox, DurableResult,
    Rejected, Failed, Indeterminate
}
public enum ReceiptStageV1 { Dispatched, Admitted, Started, Processed }
public enum ReceiptOutcomeV1 { Pending, Rejected, Failed, Indeterminate }

public sealed class ProxyReceiptGateV1
{
    private readonly ReceiptIdentityV1 expected;
    public ProxyReceiptGateV1(ReceiptIdentityV1 identity) => expected = identity;
    public ReceiptStageV1 Stage { get; private set; } = ReceiptStageV1.Dispatched;
    public ReceiptOutcomeV1 Outcome { get; private set; } = ReceiptOutcomeV1.Pending;
    public bool DurableInbox { get; private set; }
    public bool DurableResult { get; private set; }
    public bool DestinationAdmitted => Stage >= ReceiptStageV1.Admitted;

    public bool ApplyVerified(ReceiptIdentityV1 observed, ReceiptEventV1 eventKind)
    {
        if (!expected.IsValid || observed != expected || Outcome != ReceiptOutcomeV1.Pending)
            return false;
        switch (eventKind)
        {
            case ReceiptEventV1.Admitted:
                if (Stage < ReceiptStageV1.Admitted) Stage = ReceiptStageV1.Admitted;
                return true;
            case ReceiptEventV1.Started:
                if (Stage < ReceiptStageV1.Admitted) return false;
                if (Stage < ReceiptStageV1.Started) Stage = ReceiptStageV1.Started;
                return true;
            case ReceiptEventV1.Processed:
                if (Stage < ReceiptStageV1.Started) return false;
                Stage = ReceiptStageV1.Processed;
                return true;
            case ReceiptEventV1.DurableInbox:
                if (Stage < ReceiptStageV1.Admitted) return false;
                DurableInbox = true;
                return true;
            case ReceiptEventV1.DurableResult:
                if (Stage != ReceiptStageV1.Processed) return false;
                DurableResult = true;
                return true;
            case ReceiptEventV1.Rejected:
                if (Stage != ReceiptStageV1.Dispatched) return false;
                Outcome = ReceiptOutcomeV1.Rejected;
                return true;
            case ReceiptEventV1.Failed:
                if (Stage == ReceiptStageV1.Dispatched || Stage == ReceiptStageV1.Processed)
                    return false;
                Outcome = ReceiptOutcomeV1.Failed;
                return true;
            case ReceiptEventV1.Indeterminate:
                if (Stage == ReceiptStageV1.Processed) return false;
                Outcome = ReceiptOutcomeV1.Indeterminate;
                return true;
            default: return false;
        }
    }
}
