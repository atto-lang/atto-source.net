using System.Reflection;
using Ores.Net.Runtime;

/// <summary>
/// Host-runtime negative tests. These are not compiler-backed .atto execution
/// or proof of per-actor physical CLR call stacks.
/// </summary>
internal static class ActorFrameStackSelfTest
{
    public static void Run()
    {
        StackBytesAreIndependentAndZeroedOnReuse();
        ForeignActorFramesAndOffCarrierCleanupAreRejected();
        CapacityOverflowPoisonsOnlyItsActor();
        DepthOverflowPoisonsOnlyItsActor();
        ForgedFrameWithIdenticalMetadataIsNotAuthority();
        RetirementPermitsCleanupButNotNewFrames();
        StackAdmissionFailureDoesNotLeakBudget();
        Console.WriteLine(
            "PASS .NET actor-owned logical native frame stacks, depth/quota, scrub, identity and retirement");
    }

    private static void StackBytesAreIndependentAndZeroedOnReuse()
    {
        var budget = new ActorArenaBudget(4096);
        using (var arena = new ActorArena(7001, 11, 256, 128, 4, 4, 4, budget))
        using (var stack = new ActorFrameStack(arena, stackBytes: 256,
            maxFrames: 4, budget: budget))
        {
            Require(stack.ActorId == 7001 && stack.Incarnation == 11,
                "stack has exact actor incarnation identity");
            Require(arena.ReservedNativeBytes == 384 &&
                stack.ReservedNativeBytes == 256 && budget.ReservedBytes == 640,
                "separate heap and stack reservations charge the same tenant budget");
            using var turn = arena.EnterTurn(7001, 11);
            using (var root = stack.Push(turn, 128))
            {
                stack.Write(turn, root, 0, new byte[] { 1, 2, 3, 4 });
                var output = new byte[4];
                stack.Read(turn, root, 0, output);
                Require(output.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                    "root frame roundtrip");
                using (var child = stack.Push(turn, 32, alignment: 16))
                {
                    stack.Write(turn, child, 0, new byte[] { 91, 92 });
                    Require(stack.ActiveDepth == 2, "nested actor frames charged");
                }
                using (var reused = stack.Push(turn, 32, alignment: 16))
                {
                    var zero = new byte[32];
                    stack.Read(turn, reused, 0, zero);
                    Require(zero.All(b => b == 0),
                        "popped child frame storage zeroed before reuse");
                }
            }
            Require(stack.ActiveDepth == 0 && !stack.IsPoisoned,
                "balanced stack returns to empty without poisoning");
        }
        Require(budget.ReservedBytes == 0,
            "actor stack and heap quotas both refunded");
    }

    private static void ForeignActorFramesAndOffCarrierCleanupAreRejected()
    {
        using var leftArena = new ActorArena(7101, 1, 256, 128);
        using var rightArena = new ActorArena(7102, 1, 256, 128);
        using var left = new ActorFrameStack(leftArena, 256, 4);
        using var right = new ActorFrameStack(rightArena, 256, 4);
        using (var leftTurn = leftArena.EnterTurn(7101, 1))
        using (var rightTurn = rightArena.EnterTurn(7102, 1))
        {
            var frame = left.Push(leftTurn, 128);
            ExpectInvalid(() => right.Read(rightTurn, frame, 0, new byte[1]),
                "other actor cannot read frame");
            ExpectInvalid(() => left.Read(rightTurn, frame, 0, new byte[1]),
                "other actor turn is not authority");
            ExpectInvalid(() => Task.Run(frame.Dispose).GetAwaiter().GetResult(),
                "non-admitting carrier cannot pop frame");
            Require(left.ActiveDepth == 1 && !left.IsPoisoned,
                "stolen pop must not mutate owning actor's LIFO state");
            frame.Dispose();
            frame.Dispose();
            ExpectInvalid(() => left.Read(leftTurn, frame, 0, new byte[1]),
                "popped frame cannot be read");
        }
        using (var nextTurn = leftArena.EnterTurn(7101, 1))
        {
            using var newRoot = left.Push(nextTurn, 128);
            Require(left.ActiveDepth == 1, "next physical turn can reuse storage");
        }
    }

    private static void CapacityOverflowPoisonsOnlyItsActor()
    {
        using var aArena = new ActorArena(7201, 1, 256, 128);
        using var bArena = new ActorArena(7202, 1, 256, 128);
        using var a = new ActorFrameStack(aArena, 192, 4);
        using var b = new ActorFrameStack(bArena, 192, 4);
        using var turnA = aArena.EnterTurn(7201, 1);
        using var turnB = bArena.EnterTurn(7202, 1);
        using (var rootA = a.Push(turnA, 128))
        using (var rootB = b.Push(turnB, 128))
        {
            ExpectInvalid(() => a.Push(turnA, 128),
                "actor A exceeds its private byte bound");
            Require(a.IsPoisoned, "stack byte exhaustion is terminal for actor A");
            ExpectInvalid(() => a.EnsureHealthy(turnA),
                "caught actor A overflow still forces terminal fault");

            b.Write(turnB, rootB, 0, new byte[] { 83 });
            var check = new byte[1];
            b.Read(turnB, rootB, 0, check);
            Require(check[0] == 83 && !b.IsPoisoned,
                "actor B remains writable after A quota exhaustion");
        }
    }

    private static void DepthOverflowPoisonsOnlyItsActor()
    {
        using var arena = new ActorArena(7301, 1, 256, 128);
        using var stack = new ActorFrameStack(arena, 512, maxFrames: 2);
        using var turn = arena.EnterTurn(7301, 1);
        using (var root = stack.Push(turn, 128))
        using (var nested = stack.Push(turn, 16))
        {
            ExpectInvalid(() => stack.Push(turn, 16),
                "max frame depth must be enforced even with spare bytes");
            Require(stack.IsPoisoned, "depth exhaustion poisons actor incarnation");
        }
        Require(stack.ActiveDepth == 0, "poisoned frames may still LIFO-unwind");
        ExpectInvalid(() => stack.Push(turn, 16),
            "poisoned stack denies subsequent allocation");
    }

    private static void ForgedFrameWithIdenticalMetadataIsNotAuthority()
    {
        using var arena = new ActorArena(7401, 1, 256, 128);
        using var stack = new ActorFrameStack(arena, 256, 4);
        using var turn = arena.EnterTurn(7401, 1);
        using var real = stack.Push(turn, 128);
        var forged = (ActorFrame)Activator.CreateInstance(
            typeof(ActorFrame),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: new object[] { stack, turn, 0, 128, 0, 1 },
            culture: null)!;
        ExpectInvalid(() => stack.Read(turn, forged, 0, new byte[1]),
            "metadata-identical frame object is not an issued capability");
        ExpectInvalid(forged.Dispose,
            "unissued forged frame cannot pop genuine root");
        Require(stack.IsPoisoned,
            "forged LIFO pop attempt poisons the affected actor");
    }

    private static void RetirementPermitsCleanupButNotNewFrames()
    {
        using var arena = new ActorArena(7501, 1, 256, 128);
        using var stack = new ActorFrameStack(arena, 256, 4);
        using (var turn = arena.EnterTurn(7501, 1))
        {
            var frame = stack.Push(turn, 128);
            arena.Retire();
            ExpectInvalid(() => stack.Push(turn, 8),
                "retired actor cannot allocate frames");
            ExpectInvalid(() => stack.Read(turn, frame, 0, new byte[1]),
                "retired actor cannot read frames");
            frame.Dispose();
            Require(stack.ActiveDepth == 0,
                "retirement allows exact-carrier stack cleanup");
        }
        Require(arena.IsReclaimed, "retired actor heap eventually reclaims");
    }

    private static void StackAdmissionFailureDoesNotLeakBudget()
    {
        var budget = new ActorArenaBudget(384);
        using (var arena = new ActorArena(7601, 1, 256, 128,
            maxAllocations: 4, maxLifetimePins: 4, maxReadLoans: 4, budget: budget))
        {
            ExpectInvalid(() => new ActorFrameStack(arena, 256, 4, budget),
                "actor stack cannot exceed tenant's aggregate heap+stack quota");
            Require(budget.ReservedBytes == 384,
                "failed stack admission refunds the rejected reservation");
        }
        Require(budget.ReservedBytes == 0,
            "heap cleanup refunds the remaining reservation");
    }

    private static void Require(bool ok, string why)
    {
        if (!ok) throw new Exception("FAILED actor frame stack: " + why);
    }

    private static void ExpectInvalid(Action action, string why)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException) { return; }
        catch (ObjectDisposedException) { return; }
        throw new Exception("Expected actor frame stack rejection: " + why);
    }
}
