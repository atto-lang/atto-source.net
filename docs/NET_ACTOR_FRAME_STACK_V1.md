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

## 7 November hardening: terminal source-frame protocol

Additional runtime-source guards now poison the offending actor's logical
stack, even if a host callback catches the exception, on:

- duplicate `Begin` while an execution is live or suspended;
- `Resume` attempting to reuse the original physical turn, instead of a
  distinct admitted turn;
- zero/negative compiler-issued frame size;
- out-of-bounds frame reads/writes, including negative offsets and crossing
  a frame boundary.

The managed self-test adds targeted negative cases for each, plus a
two-actor aggregate-budget overcommit/rollback test. A rejected actor's
reservation cannot consume the surviving sibling's native memory credits.
These are authored host-runtime regressions, **not executed .atto guest
fixtures** or evidence of physical CLR stack-fault isolation. A stepful
managed/NativeAOT exact-head run is still mandatory before readiness.
