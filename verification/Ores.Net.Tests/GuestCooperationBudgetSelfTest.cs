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
                Require(resumed.ChargeBackedge() == (turn % 4 == 3 && i == 31),
                    "global phase must survive short resumptions");
            Require(resumed.BackedgesThisDispatch == 32, "dispatch fairness reset");
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
        // Eight 127-step turns cannot defeat the lifetime sampling cadence.
        var shortTurns = new GuestCooperationBudget(10_000);
        int charged = 0, polls = 0;
        for (int turn = 0; turn < 8; turn++)
        {
            shortTurns.BeginDispatch();
            for (int i = 0; i < 127; i++)
            {
                charged++;
                bool due = shortTurns.ChargeBackedge();
                Require(due == (charged % 128 == 0), "short-turn evasion");
                if (due) polls++;
            }
            Require(shortTurns.BackedgesThisDispatch == 127,
                "dispatch fairness count");
        }
        Require(polls == 7 && shortTurns.RemainingFuel == 10_000 - 8UL * 127,
            "short-turn sampling and fuel conservation");

        // A rejected weighted charge must not advance the slow-poll counter.
        var rejected = new GuestCooperationBudget(129);
        for (int i = 0; i < 127; i++)
            Require(!rejected.ChargeBackedge(), "unexpected early poll");
        bool denied = false;
        try { rejected.ChargeBackedge(3); }
        catch (InvalidOperationException) { denied = true; }
        Require(denied && rejected.RemainingFuel == 2, "atomic rejected charge");
        Require(rejected.ChargeBackedge(2) && rejected.RemainingFuel == 0,
            "next successful charge must reach exactly tick 128");
        Console.WriteLine("PASS .NET guest cooperation budget: 128 ticks, persistent fuel (host-only)");
    }
}
