using Ores.Net.Runtime;

internal static class GuestCooperationBudgetSelfTest
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception("Guest cooperation budget: " + message);
    }

    public static void Run()
    {
        var budget = new GuestCooperationBudget(10_000);
        int polls = 0;
        for (int step = 1; step <= 2048; step++)
        {
            bool due = budget.ChargeBackedge();
            Require(due == (step % 128 == 0),
                "expensive poll may never become per-backedge after cap");
            if (due) polls++;
        }
        Require(polls == 16 && budget.RemainingFuel == 7952,
            "exact 128 cadence and fuel consumption");

        var resumed = new GuestCooperationBudget(256);
        for (int turn = 0; turn < 8; turn++)
        {
            resumed.BeginDispatch();
            for (int i = 0; i < 32; i++)
                Require(!resumed.ChargeBackedge(), "unexpected slow path");
        }
        Require(resumed.RemainingFuel == 0,
            "a new dispatch must not refill logical fuel");
        resumed.BeginDispatch();
        bool exhausted = false;
        try { resumed.ChargeBackedge(); }
        catch (InvalidOperationException) { exhausted = true; }
        Require(exhausted, "exhausted tenant must fail closed");

        var helpers = new GuestCooperationBudget(6);
        helpers.ChargeOperation(4);
        Require(!helpers.ChargeBackedge(2), "helper charging");
        exhausted = false;
        try { helpers.ChargeOperation(1); }
        catch (InvalidOperationException) { exhausted = true; }
        Require(exhausted, "recursion and helper entry consume shared fuel");

        foreach (ulong invalid in new[] { 0UL, 100_000_001UL })
        {
            bool rejected = false;
            try { _ = new GuestCooperationBudget(invalid); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "invalid budget must fail");
        }
        Console.WriteLine("PASS .NET guest cooperation budget: 128 ticks, persistent fuel (host-only)");
    }
}
