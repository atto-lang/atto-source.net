using Ores.Net.Runtime;

internal static class CapabilityIoLifecycleV1SelfTest
{
    internal static void Run()
    {
        var gate = new HostCapabilityIoLifecycleV1(1, 2);
        Require(gate.Reserve(false, true, true, 42, 1, 55) is null, "forged actor admitted");
        Require(gate.Reserve(true, false, true, 42, 1, 55) is null, "requested grant absent");
        Require(gate.Reserve(true, true, false, 42, 1, 55) is null, "selected grant absent");
        var first = gate.Reserve(true, true, true, 42, 1, 55)
            ?? throw new Exception("FAILED: trusted local reference reservation");
        Require(gate.InFlight == 1, "credit not reserved");
        Require(gate.Reserve(true, true, true, 42, 1, 55) is null, "bounded capacity bypass");
        Require(!gate.Dispatch(first, 43, 1, 55), "wrong actor dispatched");
        Require(!gate.Dispatch(first, 42, 1, 56), "endpoint substitution dispatched");
        Require(gate.Dispatch(first, 42, 1, 55), "correct dispatch denied");
        Require(!gate.Dispatch(first, 42, 1, 55), "replayed dispatch");
        Require(gate.Cancel(first, 42, 1), "cancel missing");
        Require(!gate.Cancel(first, 42, 1), "double cancel");
        Require(!gate.Retire(first, 42, 1, false), "future cancellation retired socket");
        Require(gate.InFlight == 1, "credit refunded before physical close");
        Require(gate.Retire(first, 42, 1, true), "physical retirement rejected");
        Require(gate.InFlight == 0, "credit not released");
        var second = gate.Reserve(true, true, true, 42, 1, 55)
            ?? throw new Exception("FAILED: credit not reusable");
        Require(second.Nonce != first.Nonce, "reused stale ticket nonce");
        Require(!gate.Dispatch(first, 42, 1, 55), "stale ticket replay");
        Require(gate.RevokeActor(42, 1), "revocation failed");
        Require(!gate.Dispatch(second, 42, 1, 55), "revoked ticket dispatched");
        Require(gate.Reserve(true, true, true, 42, 1, 55) is null, "revoked actor admitted");
        Require(gate.Complete(second, 42, 1), "cancel completion unavailable");
        Require(gate.Retire(second, 42, 1, true), "cancel physical retirement rejected");
        var newer = gate.Reserve(true, true, true, 42, 2, 55)
            ?? throw new Exception("FAILED: new incarnation was blocked");
        Require(gate.Complete(newer, 42, 2), "normal completion rejected");
        Require(gate.Retire(newer, 42, 2, true), "normal physical retirement rejected");
        Require(gate.RevokeActor(42, 2), "second revocation failed");
        Require(gate.RevokeActor(43, 1), "revocation-budget exhaustion not handled");
        Require(gate.Reserve(true, true, true, 44, 1, 55) is null,
            "exhausted revocation ledger failed open");
    }

    private static void Require(bool condition, string why)
    {
        if (!condition) throw new Exception("FAILED: " + why);
    }
}
