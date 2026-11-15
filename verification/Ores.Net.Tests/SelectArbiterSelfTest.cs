using Ores.Net.Runtime;

internal static class SelectArbiterSelfTest
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("Select arbiter regression: " + reason);
    }

    private static void FirstWinnerRevokesLosersExactlyOnce()
    {
        const int count = 16;
        using var select = new SelectArbiter(count);
        var registrations = new SelectArbiter.Registration[count];
        var revoked = new int[count];

        for (var i = 0; i < count; i++)
        {
            var index = i;
            registrations[i] = select.RegisterCase(i);
            registrations[i].Arm(() => Interlocked.Increment(ref revoked[index]));
        }

        var wins = 0;
        Parallel.For(0, count, i =>
        {
            if (registrations[i].TryWin())
                Interlocked.Increment(ref wins);
        });

        Require(wins == 1, "concurrent ready race must have exactly one winner");
        var winner = select.Winner;
        Require(winner.HasValue, "winner index missing");
        var winnerIndex = winner.GetValueOrDefault();

        var revokedTotal = 0;
        for (var i = 0; i < count; i++)
        {
            var observed = Volatile.Read(ref revoked[i]);
            Require(observed == (i == winnerIndex ? 0 : 1),
                "loser revocation count mismatch at index " + i);
            revokedTotal += observed;
        }
        Require(revokedTotal == count - 1, "all and only losers are revoked");
        Require(registrations[winnerIndex].IsWinner, "winning registration identity");
    }

    private static void TerminalBeforeArmRevokesLateLoser()
    {
        using var select = new SelectArbiter(2);
        var winner = select.RegisterCase(0);
        var loser = select.RegisterCase(1);
        var winnerRevoked = 0;
        var loserRevoked = 0;

        Require(winner.TryWin(), "first case should win");
        winner.Arm(() => winnerRevoked++);
        loser.Arm(() => loserRevoked++);

        Require(winnerRevoked == 0, "winner must never be revoked as loser");
        Require(loserRevoked == 1, "late loser revoker must run immediately");
    }

    private static void CancellationRevokesArmedAndLateCases()
    {
        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        var firstRevoked = 0;
        var secondRevoked = 0;

        first.Arm(() => firstRevoked++);
        var cancellation = select.GetCancellationHandle();
        Require(cancellation.Cancel(), "cancellation handle should terminalize select");
        Require(select.IsCancelled, "cancelled terminal state");
        Require(firstRevoked == 1, "armed case revoked on cancellation");

        second.Arm(() => secondRevoked++);
        Require(secondRevoked == 1, "late-armed cancelled case revoked");
        Require(!first.TryWin() && !second.TryWin(),
            "cancelled select must not admit a winner");
        Require(!cancellation.Cancel(), "cancellation is idempotent");
    }

    private static void ConditionalCommitControlsLinearization()
    {
        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        var commits = 0;
        var firstRevoked = 0;
        var secondRevoked = 0;
        first.Arm(() => firstRevoked++);
        second.Arm(() => secondRevoked++);

        Require(!first.TryWin(() =>
        {
            commits++;
            return false;
        }), "failed readiness commit cannot win");
        Require(select.Winner is null && commits == 1,
            "failed commit leaves select pending");

        var threw = false;
        try
        {
            _ = first.TryWin(() =>
            {
                commits++;
                throw new InvalidOperationException("commit failed");
            });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Require(threw && select.Winner is null,
            "throwing commit leaves select pending");

        Require(second.TryWin(() =>
        {
            commits++;
            return true;
        }), "successful commit should win");
        Require(select.Winner == 1 && commits == 3,
            "successful commit publishes winner exactly once");
        Require(firstRevoked == 1 && secondRevoked == 0,
            "conditional winner revokes only loser");
    }

    private static void CleanupFailureCannotRollbackWinner()
    {
        using var select = new SelectArbiter(3);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        var third = select.RegisterCase(2);
        var thirdRevoked = 0;

        first.Arm(() => { });
        second.Arm(() => throw new InvalidOperationException("cleanup failed"));
        third.Arm(() => thirdRevoked++);

        Require(first.TryWin(), "winner remains successful despite loser cleanup failure");
        Require(select.Winner == 0, "winner remains terminal");
        Require(thirdRevoked == 1, "later loser cleanup still runs");
        Require(select.CleanupFailure is InvalidOperationException,
            "first cleanup failure retained for supervision");

        var observed = false;
        try { select.ThrowIfCleanupFailed(); }
        catch (InvalidOperationException) { observed = true; }
        Require(observed, "cleanup failure can be rethrown diagnostically");
    }

    private static void CommitCannotReenterArbiter()
    {
        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);
        var second = select.RegisterCase(1);
        first.Arm(() => { });
        second.Arm(() => { });

        var rejected = false;
        try
        {
            _ = first.TryWin(() =>
            {
                _ = first.TryWin();
                return true;
            });
        }
        catch (InvalidOperationException error) when (
            error.Message.Contains("must not re-enter", StringComparison.Ordinal))
        {
            rejected = true;
        }

        Require(rejected && select.Winner is null,
            "reentrant commit rejected without terminalizing select");
        Require(second.TryWin(), "another case can still win after rejected reentry");
    }

    private static void DisposeCancelsLiveRegistrations()
    {
        var select = new SelectArbiter(1);
        var registration = select.RegisterCase(0);
        var revoked = 0;
        registration.Arm(() => revoked++);

        select.Dispose();
        Require(revoked == 1, "dispose revokes live case");
        Require(select.IsCancelled, "dispose terminalizes as cancellation");
        Require(!registration.TryWin(), "disposed select cannot later win");
    }

    private static void ValidationFailsClosed()
    {
        using var select = new SelectArbiter(2);
        var first = select.RegisterCase(0);

        var duplicate = false;
        try { _ = select.RegisterCase(0); }
        catch (InvalidOperationException) { duplicate = true; }
        Require(duplicate, "duplicate case rejected");

        var range = false;
        try { _ = select.RegisterCase(2); }
        catch (ArgumentOutOfRangeException) { range = true; }
        Require(range, "out-of-range case rejected");

        first.Arm(() => { });
        var doubleArm = false;
        try { first.Arm(() => { }); }
        catch (InvalidOperationException) { doubleArm = true; }
        Require(doubleArm, "double arm rejected");
    }

    public static void Run()
    {
        FirstWinnerRevokesLosersExactlyOnce();
        TerminalBeforeArmRevokesLateLoser();
        CancellationRevokesArmedAndLateCases();
        ConditionalCommitControlsLinearization();
        CleanupFailureCannotRollbackWinner();
        CommitCannotReenterArbiter();
        DisposeCancelsLiveRegistrations();
        ValidationFailsClosed();
    }
}
