using System;
using System.Threading;
using Ores.Net.Runtime;

internal static class ActorControlDirectoryIntegrationSelfTest
{
    private static void Require(bool condition, string why)
    {
        if (!condition) throw new Exception("CONTROL integration: " + why);
    }

    internal static void Run()
    {
        long reservation = ControlPlaneArena32.RequiredNativeBytes(1);
        Require(reservation == 48, "one 32-byte record plus 16 aligned metadata bytes");
        // Budget acceptance is checked before creating a runtime dispatcher.
        bool rejected = false;
        try
        {
            using var invalid = new ActorRuntime(new ActorRuntimeOptions
            {
                UseNativeControlCounters = true,
                NativeBudgetBytes = reservation,
                MaxActors = 2
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Require(rejected, "native root control reservation must be charged to budget");

        using var arrived = new ManualResetEventSlim(false);
        var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            UseNativeControlCounters = true,
            NativeBudgetBytes = 8L * 1024 * 1024,
            MaxActors = 2
        });
        try
        {
            Require(runtime.NativeControlReservedBytes == reservation,
                "native reservation is visible from the runtime");
            Require(runtime.NativeControlLiveActors == 0,
                "root control record begins with zero live actors");
            Require(runtime.NativeControlCompletedMessages == 0,
                "root control record begins with zero completions");
            var a = runtime.Spawn(ActorKind.Private, (_, _) => arrived.Set());
            Require(runtime.NativeControlLiveActors == runtime.LiveActors &&
                    runtime.LiveActors == 1, "spawn commits one native control count");
            var b = runtime.Spawn(ActorKind.Private, (_, _) => { });
            Require(runtime.NativeControlLiveActors == 2 && runtime.LiveActors == 2,
                "second spawn publishes the bounded native count");

            runtime.Send(a, new byte[] { 42 });
            Require(arrived.Wait(TimeSpan.FromSeconds(10)), "actor carrier completed actual dispatch");
            Require(SpinWait.SpinUntil(() =>
                    runtime.CompletedMessages == 1 &&
                    runtime.NativeControlCompletedMessages == 1,
                    TimeSpan.FromSeconds(10)),
                "native completions agree with a real actor dispatch");
            runtime.Stop(b); // no scheduled turn, synchronous finalization
            Require(SpinWait.SpinUntil(() =>
                    runtime.LiveActors == 1 && runtime.NativeControlLiveActors == 1,
                    TimeSpan.FromSeconds(10)),
                "STOP removes an unqueued actor and its native control count");
            runtime.Stop(a);
            Require(SpinWait.SpinUntil(() =>
                    runtime.LiveActors == 0 && runtime.NativeControlLiveActors == 0,
                    TimeSpan.FromSeconds(10)),
                "carrier STOP quiesces and reclaims native control count");
        }
        finally
        {
            // Ensures unmanaged control root is also freed if a test assertion fails.
            runtime.Dispose();
        }
        Require(runtime.NativeControlReservedBytes == 0,
            "runtime shutdown releases native control backing");
        rejected = false;
        try { _ = runtime.NativeControlLiveActors; }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "disposed native control record cannot be dereferenced");

        using var legacy = new ActorRuntime(new ActorRuntimeOptions
        {
            UseNativeControlCounters = false,
            MaxActors = 2
        });
        Require(legacy.NativeControlReservedBytes == 0,
            "legacy actor runtime default is unaffected by optional root control");
        rejected = false;
        try { _ = legacy.NativeControlCompletedMessages; }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "non-admitted native control summary fails closed");

        Console.WriteLine("PASS actor-control-directory integration: native quota, spawn, send, STOP, shutdown (HOST ONLY; managed mailbox remains)");
    }
}
