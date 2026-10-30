using Ores.Net.Compiler;

internal static class StateRegionLoanProvenanceSelfTest
{
    public static void Run()
    {
        var borrow = new StateRegionLoanProvenance(
            9, 3, 7, 1001, 41, 6, 501,
            "inventory.stock",
            StateRegionLoanProvenance.AccessKind.Write,
            StateRegionLoanProvenance.LoanAction.Borrow);

        Require(borrow.AuthorizesRelease(borrow.ReleaseRecord()),
            "exact state-region release");

        Require(!borrow.AuthorizesRelease(new StateRegionLoanProvenance(
            9, 3, 7, 1002, 41, 6, 501, "inventory.stock",
            StateRegionLoanProvenance.AccessKind.Write,
            StateRegionLoanProvenance.LoanAction.Release)),
            "foreign logical execution rejected");
        Require(!borrow.AuthorizesRelease(new StateRegionLoanProvenance(
            9, 3, 7, 1001, 41, 5, 501, "inventory.stock",
            StateRegionLoanProvenance.AccessKind.Write,
            StateRegionLoanProvenance.LoanAction.Release)),
            "stale execution generation rejected");
        Require(!borrow.AuthorizesRelease(new StateRegionLoanProvenance(
            10, 3, 7, 1001, 41, 6, 501, "inventory.stock",
            StateRegionLoanProvenance.AccessKind.Write,
            StateRegionLoanProvenance.LoanAction.Release)),
            "foreign region rejected");
        Require(!borrow.AuthorizesRelease(new StateRegionLoanProvenance(
            9, 3, 7, 1001, 41, 6, 501, "inventory.stock",
            StateRegionLoanProvenance.AccessKind.Read,
            StateRegionLoanProvenance.LoanAction.Release)),
            "wrong access mode rejected");

        Expect<ArgumentOutOfRangeException>(() => _ =
            new StateRegionLoanProvenance(
                9, 3, 7, 1001, 0, 0, 501, "inventory.stock",
                StateRegionLoanProvenance.AccessKind.Write,
                StateRegionLoanProvenance.LoanAction.Borrow),
            "unbound execution handle rejected");

        Console.WriteLine("PASS .NET state-region loan provenance v1");
    }

    private static void Require(bool value, string why)
    {
        if (!value) throw new Exception("FAILED: " + why);
    }

    private static void Expect<T>(Action action, string why) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("FAILED expected " + typeof(T).Name + ": " + why);
    }
}
