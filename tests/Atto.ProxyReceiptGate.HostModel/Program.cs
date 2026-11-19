using Atto.Runtime.Proxy;
using System;

static class Program
{
    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("proxy receipt gate regression");
    }
    private static int Main()
    {
        var id = new ReceiptIdentityV1(1, 11, 12, 13, 14, 17, 15, 16, 1);
        var gate = new ProxyReceiptGateV1(id);
        Check(!gate.DestinationAdmitted);
        Check(!gate.ApplyVerified(id, ReceiptEventV1.Started));
        Check(!gate.ApplyVerified(id, ReceiptEventV1.Processed));
        Check(!gate.ApplyVerified(id, ReceiptEventV1.DurableInbox));
        Check(!gate.ApplyVerified(id, ReceiptEventV1.DurableResult));
        Check(!gate.ApplyVerified(id with {Tenant = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {CallerIncarnation = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {TargetIncarnation = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {AssociationGeneration = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {PlacementEpoch = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {Attempt = 100}, ReceiptEventV1.Admitted));
        Check(!gate.ApplyVerified(id with {ProtocolMajor = 2}, ReceiptEventV1.Admitted));
        Check(gate.ApplyVerified(id, ReceiptEventV1.Admitted));
        Check(gate.DestinationAdmitted);
        Check(!gate.ApplyVerified(id, ReceiptEventV1.DurableResult));
        Check(gate.ApplyVerified(id, ReceiptEventV1.DurableInbox));
        Check(gate.DurableInbox && gate.Stage == ReceiptStageV1.Admitted);
        Check(gate.ApplyVerified(id, ReceiptEventV1.Started));
        Check(gate.ApplyVerified(id, ReceiptEventV1.Admitted));
        Check(gate.Stage == ReceiptStageV1.Started);
        Check(gate.ApplyVerified(id, ReceiptEventV1.Processed));
        Check(gate.ApplyVerified(id, ReceiptEventV1.DurableResult));
        Check(gate.DurableResult);
        Check(!gate.ApplyVerified(id, ReceiptEventV1.Failed));
        var missing = new ProxyReceiptGateV1(id);
        Check(missing.ApplyVerified(id, ReceiptEventV1.Indeterminate));
        Check(missing.Outcome == ReceiptOutcomeV1.Indeterminate);
        Check(!missing.ApplyVerified(id, ReceiptEventV1.Admitted));
        var rejected = new ProxyReceiptGateV1(id);
        Check(rejected.ApplyVerified(id, ReceiptEventV1.Rejected));
        Check(!rejected.ApplyVerified(id, ReceiptEventV1.Admitted));
        var failed = new ProxyReceiptGateV1(id);
        Check(failed.ApplyVerified(id, ReceiptEventV1.Admitted));
        Check(failed.ApplyVerified(id, ReceiptEventV1.Failed));
        Check(!failed.ApplyVerified(id, ReceiptEventV1.Processed));
        var invalid = new ProxyReceiptGateV1(id with {Tenant = 0});
        Check(!invalid.ApplyVerified(id with {Tenant = 0}, ReceiptEventV1.Admitted));
        Console.WriteLine("HOST_MODEL_ONLY proxy receipt v1 assertions passed");
        return 0;
    }
}
