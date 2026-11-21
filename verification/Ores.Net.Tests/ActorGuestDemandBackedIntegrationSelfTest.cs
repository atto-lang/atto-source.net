using System.Buffers.Binary;
using Ores.Net.Runtime;

// Compiled Node guest admission needs exact native memory receipts under both
// eager and optional demand-backed backing. This runs the real actor host runtime;
// it is NOT compiler-produced AttoLang .atto guest execution.
internal static class ActorGuestDemandBackedIntegrationSelfTest
{
    public static void Run()
    {
        var options = new ActorRuntimeOptions
        {
            MaxActors = 2,
            MessagesPerDispatch = 1,
            MailboxMessages = 8,
            MailboxBytes = 4096,
            ActorPersistentBytes = 512,
            ActorScratchBytes = 256,
            ActorStackBytes = 64,
            ActorMaxStackFrames = 8,
            ActorMaxAllocations = 16,
            ActorMaxLifetimePins = 8,
            ActorMaxReadLoans = 8,
            NativeBudgetBytes = 16 * 1024 * 1024,
            DemandBackedActorHeaps = true,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
                QueueCapacityPerDomain = 8
            }
        };

        using var runtime = new ActorRuntime(options);
        Require(runtime.NativeReservedBytes == 0,
            "demand-backed actor startup must not reserve logical slab bytes");
        Exception? failure = null;
        using var received = new CountdownEvent(1);
        ActorRef actor = runtime.Spawn(ActorKind.Private, (context, message) =>
        {
            try
            {
                context.RequireOwnedGuestStorageAbiV1("generation16_slot16");
                Require(message.Length == 1, "actual mailbox receive shape");
                var layout = new ActorGuestLayout32(0xA711u, 16,
                    new[] {
                        new ActorGuestField32(0, ActorGuestFieldKind32.I64),
                        new ActorGuestField32(8, ActorGuestFieldKind32.Ref32, 0xA711u)
                    });
                ActorGuestOwnedRef32 root =
                    context.AllocateOwnedTypedGuestObject32(layout);
                Require(!root.IsNull, "native demand-backed guest must be non-null");
                Require(runtime.NativeReservedBytes > 0,
                    "native bytes must be physically charged before publishing Ref32");
                Span<byte> scalar = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(scalar, 42);
                context.WriteOwnedGuestObject32(root, 0, scalar);
                context.WriteOwnedGuestReference32(root, 8, default);
                scalar.Clear();
                context.ReadOwnedGuestObject32(root, 0, scalar);
                Require(BinaryPrimitives.ReadInt64LittleEndian(scalar) == 42,
                    "demand-backed typed native guest field roundtrip");
                context.DropOwnedGuestObject32(root);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure, ex);
                throw;
            }
            finally
            {
                received.Signal();
            }
        });

        Require(runtime.NativeReservedBytes == 0,
            "spawning a dynamic actor must not materialize an unused slab");
        runtime.Send(actor, new byte[] { 1 });
        Require(received.Wait(TimeSpan.FromSeconds(15)),
            "demand-backed native guest actor did not execute");
        Require(SpinWait.SpinUntil(() =>
            runtime.CompletedMessages == 1 || runtime.FaultedTurns != 0,
            TimeSpan.FromSeconds(15)), "guest turn did not finish");
        Require(Volatile.Read(ref failure) is null && runtime.FaultedTurns == 0,
            "actor-native Ref32 allocation/read/write/drop failed");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(() =>
            runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(15)),
            "actor retirement must release demand-backed native slabs");
        Console.WriteLine(
            "PASS actor-guest-demand-backed-ref32: first guest allocation charges physical native storage and actor stop refunds it (host only)");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException("ActorGuestDemandBackedIntegrationSelfTest: " + reason);
    }
}
