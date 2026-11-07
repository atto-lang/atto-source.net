using System.Buffers.Binary;
using Ores.Net.Runtime;

// These are *static, compiled* MethodDef calls from OresGenerated.dll. The
// generated assembly is a build-time dependency of this NativeAOT executable;
// no Reflection.Emit, Assembly.Load, JIT or managed DLL sidecar is permitted.
static long Initial() => Oreslang.Generated.OresProgram.ActorInitial();
static long Step(long current, long amount) =>
    Oreslang.Generated.OresProgram.ActorStep(current, amount);

static void Require(bool valid, string reason)
{
    if (!valid) throw new InvalidOperationException("Actor M0 CIL probe: " + reason);
}

static bool Wait(Func<bool> ready) =>
    SpinWait.SpinUntil(ready, TimeSpan.FromSeconds(10));

var options = new ActorRuntimeOptions
{
    MaxActors = 4, MessagesPerDispatch = 8,
    MailboxMessages = 8, MailboxBytes = 512,
    ActorPersistentBytes = 4096, ActorScratchBytes = 512,
    ActorStackBytes = 256, ActorMaxStackFrames = 4,
    ActorMaxAllocations = 16, ActorMaxReadLoans = 4,
    ActorMaxLifetimePins = 4, NativeBudgetBytes = 65536,
    Dispatcher = new ActorDispatcherOptions
    {
        PrivateWorkers = 1, SharedWorkers = 2, UntrustedWorkers = 1,
        QueueCapacityPerDomain = 4
    }
};

Require(Initial() == 0, "generated actor field initializer is compiled");
using var runtime = new ActorRuntime(options);
using var first = new CountdownEvent(2);
using var second = new CountdownEvent(2);
var values = new long[2];
var counts = new int[2];

ActorMessageHandler MakeHandler(int index)
{
    // Each Spawn gets its own closure and ActorArena; no state allocation or
    // frame capability ever crosses the actor/channel boundary.
    ActorArenaHandle? state = null;
    return (context, message) =>
    {
        Require(message.Length == 8, "typed scalar actor mail has exact width");
        if (state is null)
        {
            state = context.AllocatePersistent(sizeof(long));
            Span<byte> initialize = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(initialize, Initial());
            context.Write(state, 0, initialize);
        }

        // Exact-turn frame storage charges the guest call descriptor. The
        // generated ActorStep CIL itself is direct and nonrecursive; this
        // bridge is NOT arbitrary actor-method/frame lowering or proper TCO.
        using var frame = context.PushFrame(32);
        Span<byte> currentBytes = stackalloc byte[sizeof(long)];
        context.Read(state, 0, currentBytes);
        long previous = BinaryPrimitives.ReadInt64LittleEndian(currentBytes);
        long amount = BinaryPrimitives.ReadInt64LittleEndian(message.Span);
        Span<byte> invocation = stackalloc byte[2 * sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(invocation, previous);
        BinaryPrimitives.WriteInt64LittleEndian(invocation[sizeof(long)..], amount);
        context.WriteFrame(frame, 0, invocation);

        // This call resolves to the source-compiled checked-add MethodDef.
        long next = Step(previous, amount);
        BinaryPrimitives.WriteInt64LittleEndian(currentBytes, next);
        context.Write(state, 0, currentBytes);
        values[index] = next;
        int count = Interlocked.Increment(ref counts[index]);
        if (count == 1) first.Signal();
        else if (count == 2) second.Signal();
        else if (count == 3 && index == 1)
        {
            // Post-fault survivor message: publication is observed through
            // native actor state/CompletedMessages, not a synthetic signal.
        }
        else throw new InvalidOperationException("Unexpected actor mail count");
    };
}

var left = runtime.Spawn(ActorKind.Shared, MakeHandler(0));
var right = runtime.Spawn(ActorKind.Shared, MakeHandler(1));
Require(runtime.LiveActors == 2 && runtime.StartedCarrierThreads == 0,
    "two distinct actor heaps are admitted without creating carriers");
Require(runtime.NativeReservedBytes == 2L * (4096 + 512 + 256),
    "two independent actor heaps and frame stacks are budgeted");

void SendAmount(ActorRef destination, long amount)
{
    Span<byte> message = stackalloc byte[sizeof(long)];
    BinaryPrimitives.WriteInt64LittleEndian(message, amount);
    runtime.Send(destination, message);
}

SendAmount(left, 1);
SendAmount(right, 5);
Require(first.Wait(TimeSpan.FromSeconds(10)), "first actor mail phase completed");
Require(values[0] == 1 && values[1] == 5,
    "generated actor CIL maintained two distinct actor state cells");

SendAmount(left, 2);
SendAmount(right, 7);
Require(second.Wait(TimeSpan.FromSeconds(10)), "second actor mail phase completed");
Require(values[0] == 3 && values[1] == 12,
    "two compiled actor methods independently update actor-owned native heaps");
Require(Wait(() => runtime.CompletedMessages == 4 && runtime.FaultedTurns == 0),
    "no actor faults or lost messages during generated MethodDef execution");

// Force checked add.ovf in *generated source CIL*, not in a host simulator.
// The runtime must fault/retire only the overflowing actor incarnation.
SendAmount(left, long.MaxValue);
Require(Wait(() => runtime.FaultedTurns == 1 && runtime.LiveActors == 1),
    "generated checked CIL overflow retires only actor A");
SendAmount(right, 1);
Require(Wait(() => runtime.CompletedMessages == 5 && values[1] == 13),
    "actor B continues after actor A generated CIL overflow");
Require(values[0] == 3 && runtime.FaultedTurns == 1,
    "actor A fault did not corrupt actor B or signal extra success");

runtime.Stop(right);
Require(Wait(() => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0),
    "both actor native heaps, frames and quotas return to zero");

Console.WriteLine(
    "ACTOR_M0_NATIVEAOT_GENERATED_CIL_EXECUTED|actors=2|left=3|right=13|faulted=1|survivor=1|frames=charged|heap=isolated");
Console.WriteLine(
    "ACTOR_M0_SCOPE=one_i64_field_one_checked_method|not_full_actor_lowering|not_physical_stack_isolation");
