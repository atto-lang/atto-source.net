# .NET actor frame unification — compiler-owned guest-fault protocol v1

Status: **source-authored, NOT_EXECUTED on a hosted runner** until the exact
head completes real managed and NativeAOT checks with nonzero executed steps.
This is a host/runtime seam for future compiler lowering, not evidence that
an AttoLang `.atto` actor has compiled or run.

## Canonical design decision

Use the existing current-main **ActorArena-backed `ActorFrameStack`** and
`ActorStackFrameHandle` as the single authoritative logical frame allocator.
Do not blindly replace it with the independent unmanaged slab proposed by
the older conflicting draft #385. Per-actor heaps remain separate because
each `ActorRuntime.ActorCell` already owns a distinct `ActorArena`.
Frames are a bounded region of that actor's persistent native arena, not
an additional native reservation that could double-charge or fragment memory.

Separate **two intentional error contracts**:

1. `ActorContext.PushStackFrame`, `ReadStackFrame`, `WriteStackFrame`
   and `PopStackFrame` preserve the current failure-neutral host contract.
   Invalid host input throws, but the caller can catch and recover without
   corrupting existing live frames. Host frame handles are exact-issued,
   bounded, zeroed on pop, and denied by the JSON no-wire converter.
2. **Compiler-facing guest-only operations** `GuestPushStackFrame`,
   `GuestReadStackFrame`, `GuestWriteStackFrame` and
   `GuestPopStackFrame` use the same underlying arena, with one additional
   per-handler fault latch and LIFO issuance list. A malformed guest frame
   length/alignment, out-of-bounds guest read/write, unknown/host-owned
   handle, wrong pop order, or stack admission failure permanently faults
   the **current synchronous guest handler** even if a C# callback catches
   the original exception. Source lowering must call these operations,
   not the host-recoverable variants.

`ActorRuntime.RunCell` calls `ActorContext.ValidateGuestFrameExit` before
leaving the current physical turn. It stops only the offending actor if the
guest latch is faulted or there are unbalanced compiler frames. The
underlying ActorArena continues to own all native byte storage and performs
the existing quiescent STOP/reclamation protocol. Replayed/off-carrier
`ActorContext` credentials are rejected by validating the original
`ActorArenaTurn` **before** any guest-latch mutation; a stolen context
cannot poison a healthy actor via a foreign thread.

## Regression coverage

`ActorGuestFrameGateSelfTest` exercises five independent terminal guest
failures: swallowed frame bounds error, live frame on synchronous handler
return, a host-issued handle smuggled into guest API, incorrect guest pop
order, and swallowed compiled guest frame-count exhaustion. A healthy sibling
on the SHARED worker continues: it catches a
recoverable host bounds error, correctly pushes/writes/reads/pops a guest
frame, and returns 42. The test also verifies off-carrier denial before
fault-latch mutation, rejection of expired actor contexts, exact faulted
and completed turn counts, and final zero native budget after STOP.

## Explicitly not implemented

- Compiler-generated `.atto` typed call-frame layouts, locals/spills,
  return PCs, stack maps, ownership or return-value lowering.
- Source-level await, task suspension/resumption or saved guest frames across
  handler boundaries. Guest frames must be balanced on every synchronous
  handler exit until an owned continuation protocol is implemented.
- CLR physical carrier-stack switching, process-level StackOverflowException
  containment, or a separate guarded fiber stack.
- Automatic conversion of every existing host frame call to guest semantics.
  Only newly lowered compiler-owned guest calls should use the guest methods.

Promotion needs independent review, a stepful exact-head managed test and
Linux/Windows NativeAOT host probe, compiler-backed actor-A quota fault /
actor-B `42` / supervisor restart fixtures and explicit cross-backend
receipts. `steps=null` GitHub Actions failures prove runner admission
failed, **not** that the tests passed or failed.
