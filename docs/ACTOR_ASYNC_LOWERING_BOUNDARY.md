# Actor async lowering boundary (candidate)

## Implemented host path

The opt-in `OresVmHost.ActorScheduler.RunPumpAsync` runs one selection/segment
per queued item on the existing `ActorDispatchDomains.Private` pool for the
tenant. Default worker cap is four, explicitly configurable at trusted isolate
creation (1..256). Queue capacity is the tenant actor limit. There is at most
one queued or executing item per pump. Readiness is awaited by the orchestration
task after the carrier has relinquished the actor's lease. Cancellation never
abandons an already admitted worker item. Manual external dequeue/admission is
fenced while the pump owns an actor. The dedicated pool is joined outside the
host gate on isolate destruction.

This is a trusted host profile, not an enforced hostile-tenant VM, and not a
replacement for the separate `ActorRuntime` physical actor ABI.

## Compiler audit

* `ActorIncrementSource.cs` verifies one synchronous increment, with a literal
  or owned i64 mail argument. `ActorIncrementSourceEmitter.cs` emits static C#
  against synchronous host dispatch and the distinct ActorRuntime path.
* `DeclaredActorCounterV2.cs` checks five canonical declared-actor units and
  a closed synchronous external receive. The async main unit is not compiled
  by that entry point.
* The normal compiler's source admission still rejects unsupported actor
  suspension; do not weaken it simply to admit text containing await.

The first executable async actor compiler slice must use the canonical
declared actor/receive grammar and its accepted suspends modifier, not invent
another actor declaration dialect. Keep lowering behind a separate explicit
compiler capability until the source/type/effect and ownership checks pass.

## Required executable slice

1. Verify an owned scalar message and an explicitly typed awaitable, with
   a bounded control-flow graph and one suspension edge. Reject await in
   critical state access, an escaping state borrow, unknown effects and an
   implicit await in a non-suspending method.
2. Generate a static AOT-compatible frame with only validated owned live
   locals, a bounded program counter and an explicit continuation branch.
   Never store ActorDispatchScope, state loans, a physical lease, raw mailbox
   authority or a host scheduler in a guest frame.
3. Bridge the verified frame to the trusted pump with a fresh scope per
   segment. A readiness task is not authority. A fault or cancellation must
   follow typed guest effect semantics; the current host pump faults and
   drains on unhandled readiness failure rather than implementing this ABI.
4. Require generated source identity plus actual compiled .atto execution:
   ordinary receive B interleaves between A's segments, serial B cannot;
   actor-local result 42; stale callback, foreign ticket, borrow-across-await
   and non-suspends await negatives. Exercise CoreCLR and standalone NativeAOT.
   A reference-model pass or a zero-step Actions job is not this proof.

Do not claim source-level async actor parity until these receipts exist.
