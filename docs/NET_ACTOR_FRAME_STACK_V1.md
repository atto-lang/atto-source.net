# .NET NativeAOT actor-owned frame slabs — M1 host/runtime slice

Status: **implementation authored, execution NOT VERIFIED**. The source is
part of the AttoLang (formerly Oreslang) runtime. This is not a guest-stack
parity claim with Java/GraalVM or LLVM and is not physical CLR/OS stack-fault
containment.

## Implementation

- Each `ActorRuntime.ActorCell` owns one distinct `ActorArena` (already on
  `main`) and one `ActorFrameStack` for its exact actor incarnation. The
  frame slab is reserved lazily, so actors that never enter compiler frame APIs
  do not consume a second native slab.
- The stack is separately allocated with `NativeMemory.AllocZeroed`,
  bounded by `ActorMaxFrames` (default 64) and `ActorFrameBytes` (default
  64 KiB), and charged against the runtime's aggregate native budget **and**
  the process budget before bytes are allocated. Each frame is an exact-issued
  reference; byte access copies bounded spans, never returns a native pointer,
  and never exposes a frame on an actor message.
- `Begin` binds one logical execution to an authenticated
  `ActorArenaTurn`. `Push/Pop` enforce a strict LIFO byte stack, with frame
  reuse scrubbed before it is issued. `Write/Read` reject stale, foreign, or
  out-of-bounds access; frame-byte and frame-depth exhaustion permanently
  poison that actor's frame stack.
- `Suspend` preserves frame bytes while revoking the previous physical-turn
  authority. `Resume` accepts the exact one-shot continuation identity and
  original execution on a new admitted actor turn, including a different
  carrier thread. Suspension and resumption are a **trusted host scheduler
  seam only**; the synchronous shipping `ActorContext` deliberately exposes
  no suspension method, to avoid silently making handlers reentrant.
- `ActorContext` offers synchronous frame begin/push/read/write/pop/finish
  operations, but no guest suspension. After each handler returns,
  `ActorRuntime` calls `ValidateSynchronousTurnExit`: swallowed quota
  exceptions, live frames, and even parked continuations are terminal.
  The separate `ValidateTurnExit` host-scheduler seam permits suspension
  only for the exact physical turn that issued it, not a fresh/reused turn.
  This prevents a fake suspended handler from accepting another mailbox
  message before a verified scheduler exists.
- `Stop` seals further frame admission. Actor finalization zeroes and frees
  the native slab, refunds its native quota exactly once, and leaves sibling
  stacks unchanged. No raw frame pointer is exported, so operations cannot
  dereference freed memory after finalization.
- `ActorFrameStack` is a privileged compiler/runtime primitive, not a
  guest-visible C# implementation of AttoLang core/std algorithms.

## STOP, authority and synchronous-handler hardening

This stacked integration preserves PR #385's terminal quota and single-claim
continuation substrate while incorporating the useful boundary protections
identified during the competing PR #384 review; it does **not** combine the
two independent allocator implementations.

- STOP permanently seals new frame admission. An already admitted physical
  carrier can still `Pop` and `Finish` its existing execution frames after
  `ActorArena.Retire` because these two cleanup operations validate the
  *physical turn* without granting new allocation authority. `Begin`,
  `Push`, `Read`, `Write`, `Suspend`, and `Resume` remain closed.
- A foreign thread, expired turn, stale execution ticket or incorrect
  incarnation cannot use the cleanup exception. The validated physical carrier
  completes cleanup and then normal quiescence/reclaim occurs.
- `ActorFrameExecution`, `ActorFrameHandle` and
  `ActorFrameContinuation` explicitly deny JSON serialization and
  deserialization. Other transports must maintain the same no-wire rule.
- Host C# `async void` handlers are rejected at `Spawn` before allocating
  actor resources, including when hidden inside multicast delegates. This
  uses compiler-emitted `AsyncStateMachineAttribute` metadata: a **native
  build must execute the guard test** before its behavior under trimming can
  be relied upon. It does not prove every arbitrary asynchronous host
  callback is safe.

`ActorFrameStackSelfTest` now also checks STOP during a live frame turn,
in-flight frame unwind, off-carrier cleanup denial, explicit JSON no-wire
rejection, and direct/multicast async-void admission denial. The independent
Native AOT actor-frame probe and the workflow assert the async guard receipt.

## Regression coverage

`ActorFrameStackSelfTest` is called by the repository's canonical
`verification/Ores.Net.Tests` entrypoint. Source-authored checks cover
independent slabs, copy semantics, zeroed frame reuse, strict LIFO, terminal
quota failure, no swallowing of frame failures, cross-carrier
suspend/resume, old-generation continuation rejection, idempotent
retirement, aggregate budget refund, synchronous-handler suspended escape
denial, wrong-physical-turn suspension denial, and actor-A failure with
actor-B survival on one SHARED carrier.

`verification/Ores.Net.ActorFrameProbe` is a separate trim/AOT-safe host
executable. The existing `actor-memory-native-aot.yml` source-local matrix
now publishes and executes it on Linux x64 and Windows x64, requiring an
exact pass marker. The managed test entrypoint also runs as before.

The workflow *must* be treated as **NOT_EXECUTED** when jobs have
`runner_id=0`, `steps=[]`, `steps=null` or no successful test/publish steps.
A source-only commit or artifact upload is not a NativeAOT execution receipt.

## Compiler-owned typed frame layout planning (pre-lowering)

The opt-in typed CFG compiler now exposes
`CfgActorFrameLayoutPlanner.Plan(VerifiedCfgFunction, maximumFrameBytes)`.
It accepts only frozen, already-verified SSA. Bool and i64 preserve their
distinct types and use aligned, deterministic eight-byte slots indexed by
dense SSA identity. Planning rejects excess frame bytes, missing/duplicate
IDs and unsupported scalar kinds before any generated CIL is emitted.

`CfgActorFrameLayoutSelfTest` exercises deterministic slot offsets,
post-verification input mutation, and a configured frame-byte budget that
fails closed. These are **compile-time layout tests**, not a guest compiler
using `ActorFrameStack`. The actual CIL emitter still emits CLR locals and
uses CLR carrier stacks. Future work must connect the plan to real typed
spill/restore, ownership/drop scheduling, source `.atto` actor methods and
continuations; `Plan` alone must never be recorded as guest parity.

## Parity not yet established

1. The .NET scalar and structured frontends still reject actors, classes,
   actor-local access, asynchronous suspension and most related source
   constructs. **No canonical `.atto` actor call-frame lowering** has been
   implemented. NativeAOT host tests do not prove guest execution.
2. A logical native byte slab is **not** a native/fiber/guard-paged execution
   stack. Managed C# handler recursion and generated CIL still use a CLR
   carrier stack. An actual `StackOverflowException` may terminate the
   process; this implementation does not claim to catch it.
3. The frame slab holds opaque bytes, not a generated typed frame layout,
   return PCs, register roots, stack maps, checked affine loans, or
   `SourceTask` continuations. Native/FFI execution and optimizer tail calls
   need separate evidence.
4. Each actor stack is one logical execution at a time. A full multi-task
   scheduler must add compiler-owned task selection, parking and wakeup
   fencing, without executing another handler while its actor's turn is
   suspended. Generation and cancellation must be authenticated at this seam.
5. There is no independent Java, LLVM, Forge/Cranelift or Wasm compiled-guest
   receipt associated with these new .NET changes.

## Required next acceptance gates

- Run real `dotnet run --project verification/Ores.Net.Tests -c Release`
  and both platform NativeAOT host probes at the **exact PR head**, verifying
  nonzero executed steps, head SHA, tree SHA, and output logs.
- Add compiler-owned frame descriptors, reserved before guest calls, and
  actual guest local/return/spill lowering. Ensure all paths including
  exceptions, cancellation, `defer`, actor STOP, and mutual tail calls
  execute the correct stack discipline.
- Prove a canonical `.atto` guest test: bounded recursion in actor A fails
  safely, actor B responds on its existing shared carrier, STOP/restart
  invalidates A's old frame tokens, and suspended frames resume on a distinct
  carrier. Use a deliberately reduced **native OS stack limit** and
  guard-page/OS-process isolation for calls not yet compiled to bounded
  continuation frames. Do not market logical frame bounds as native fault
  containment.
- Compare exact head/tree and compiler-backed evidence independently with
  Java PR #2627, LLVM and Forge actor heap/runtime branches, and the shared
  platform-conformance actor-stack contract (#562). Retain independent
  approval and private-source CI export gates.

## Canonical typed spill and suspension-epoch hardening

The follow-up canonical actor-frame API supports immutable 1–128-slot
`ActorFrameTypedLayout` descriptors with `i64` and `bool` types.
Each scalar occupies an eight-byte little-endian slot. Only typed APIs may
read or write typed frames; the existing raw byte operations are explicitly
denied on typed frames, preventing forged bool contents or scalar kind
confusion. Untyped host frame handles cannot access typed slots. Descriptors
are snapshots of caller-provided types, not mutable aliases.

The existing exact-issued `ActorFrameContinuation` is now additionally
fenced to the physical-turn epoch in which suspension occurred.
A continuation must resume on a **strictly later authenticated arena turn**;
attempting to reclaim it within the original carrier turn poisons only that
actor's logical frame stack. No CLR machine stack is suspended or switched.

The `CfgActorFrameLayoutSelfTest` now also parses real structured AttoLang
function source through the existing verified CFG lowering, derives a
compiler-owned scalar slot layout, and feeds it into an actor's canonical
private native frame slab for a host-only typed round-trip. This is **not**
generated guest CIL spill/restore, and actor/await source remains unsupported.

The expanded `ActorFrameStackSelfTest` covers typed schema snapshotting,
negative unknown kinds, raw-byte type bypass attempts, wrong-kind and
out-of-range accesses, untyped token confusion, and same-physical-turn
continuation replay. Execution remains **NOT_EXECUTED** until SDK and
exact-head NativeAOT runners actually run these tests.
