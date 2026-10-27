# Closed root Task<int> / Future<int> source slice

Experimental opt-in profile, stacked on #186. Full interop issue #183 and the
62 shared vectors remain pending. This profile does not enable general Task,
Observable or actor syntax in the shipping scalar/structured compiler.

Accepted shape:

```oreslang
pub fnc main(): int {
    const Future<int> f = Future.from_task(task { return 6 * 7; });
    return await f;
}
```

`let` also binds the single view. Payload is signed i64, copyable by construction.
The producer uses checked, capture-free scalar addition, subtraction and multiplication (4096 verified
operations maximum). Strings, other generics, captures/borrows, effects, loops,
producer await, actors, nested tasks, extra statements and mismatched awaited
bindings fail before emission. General names do not grant runtime capability.

`oresnet check-task-i64` validates the closed program and existing verified SSA.
`oresnet compile-task-i64 ... --out OresGenerated.dll` emits the producer as
`RunTaskI64Producer() -> i64`, with checked CIL arithmetic. The dedicated static
NativeAOT host supplies the root Task-start / Future-projection / await / join
entry shim. It statically calls the compiled method through a delegate. This is
source-connected compiled execution, not source interpretation, runtime codegen
or arbitrary async-method lowering. Ordinary `check/compile` stays fail-closed.

Runtime nominal Owner and Future capabilities are distinct. The owner alone may
cancel before claim and owns a separate body-retirement receipt. The source root
creates one observer and awaits once; the observer contains no owner capability.
Observer cancellation detaches its registration and cannot cancel the producer.
The root joins the actual producer's body-return receipt even on failure.
Faulted cancellation-named exceptions remain failed; only preclaim owner
cancellation creates authentic cancellation. The closed guest body can currently
fail only through checked arithmetic, not throw/raise/panic syntax.

One shared pump drains a fixed-capacity 64-entry root queue, handing its carrier
back to the pool after at most eight producer turns when more work remains. Capacity
includes executing producers; no executor or OS thread is created per Task.
No ExecutionContext/actor authority flows into its callback. Each admitted source
producer is finite and non-suspending; cooperation is at producer return. This
is a soft-RT root milestone, not a general resumable scheduler, actor-affine
continuation model, hard-RT executor, generation fence or arena reclaim proof.
PhysicalDone certifies producer body return/closure release, not all arbitrary
external notification drains or actor/FFI quiescence.

Verification script: `verification/assert-task-i64-source.ps1` links and runs
source result 42 and checked overflow through a real NativeAOT compiler and
dedicated NativeAOT executable, plus eight profile-specific rejection cases.
Receipts bind source bytes, producer CIL, native binaries, output and failure.
Runtime tests separately exercise preclaim owner cancellation, observer-only
cancellation while a producer is live, and cancellation-named fault identity.
Execution evidence is recorded in the PR; no native pass is inferred from this
document or an old #186 receipt. Keep draft until exact-head execution.
