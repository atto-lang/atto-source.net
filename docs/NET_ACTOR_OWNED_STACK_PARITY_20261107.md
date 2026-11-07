# AttoLang .NET actor-private heaps and frame-stack parity — November 7, 2026

## Scope and evidence classifications

This branch extends the **existing .NET NativeAOT actor-owned unmanaged heap** with
an **independently allocated, bounded, actor-incarnation-owned unmanaged logical
frame stack**. These are two separate native memory slabs and are jointly
charged against the actor runtime/tenant budget and global process budget.

**Implemented host runtime substrate:** `ActorArena`, `ActorFrameStack`,
`ActorRuntime` and `ActorContext.PushFrame/ReadFrame/WriteFrame`.
**Not demonstrated:** compiled AttoLang actor guest frame lowering, physical
.NET/CLR stack switching, and actor-safe containment of a general CLR
`StackOverflowException`. Tests are source code until an exact-head admitted
run executes them. A `steps=[]` or `steps=null` failure is **runner
admission failure**, never proof of a failing C# test.

### Comparison with current Java / LLVM branches

| Property | Java/GraalVM | LLVM | .NET NativeAOT this branch |
|---|---|---|---|
| Separate actor-owned heap bytes | Java native/off-heap actor arena substrate, guest-placement proof separate | ActorOwnedHeap/graph/native actor arena substrate | Existing `ActorArena` persistent and scratch unmanaged slabs |
| Per-actor logical frame storage | Java [PR #2627](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/2627) draft, default 64 frames / 64 KiB, managed frame arrays | Various native actor heap/continuation lifetime substrates; physical stack parity not assumed | **New** actor-owned native frame slab, default 64 frames / 64 KiB |
| Frame memory quota and process budget | Draft frame arena and depth admission, subject to guest verification | Native per-actor/tenant heap quotas and resource fencing | Runtime + process budget reservation; bounded byte and frame depth |
| Exact actor incarnation / carrier | Java `ExecutionContext`/frame-token checks in draft | Native actor generation/fencing contracts | Exact `ActorArenaTurn`, actor/incarnation and carrier checks; opaque issued frame refs |
| LIFO and stale handles | Java frame lease cleanup in draft | Native ownership/lifetime checks | New LIFO pop, frame zeroing, stale token rejection, wrong-carrier refusal |
| Overflow containment | Draft bounded frame quota, **not physical JVM overflow proof** | Physical native stack isolation must be independently proven | Caught **explicit frame quota** violation marks actor terminal, sibling survives; **not CLR recursion isolation** |
| Source guest call frames | Java [PR #2628](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/2628) draft descriptor charging | Native scalar/actor compiler work separate | **Not yet compiler-lowered**, host handler root and explicit frame API only |
| Stackless scalar calls / tail recursion | Java [PR #2633](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/2633) restricted draft | Backend native call lowering and proper TCO remain separate acceptance gates | **Not implemented** for actor guest code |
| Suspend/resume while retaining frames | Java #2627 draft with suspend leases | Native pinned continuation/heap work | **Not implemented**; frame tokens intentionally bound to one physical turn |
| OS process survival from arbitrary native/managed recursion | **Unproven** | **Unproven without guard and fault policy receipt** | **Not provided by CLR host callbacks** |
| Exact-head compiler-backed actor frame evidence | Draft claims not promoted without executable receipts | Needs specific native guest receipt | **Unproven**; managed/self-test and NativeAOT host probe only |

Java PR status and contents are *comparison targets*, not evidence of passing
upstream guest CI. Do not silently turn in-progress Java features into .NET
parity claims.

## Implemented invariants

1. Each spawned actor gets its own `ActorArena` heap and independent
   `ActorFrameStack` with separate native allocation, per-instance identity,
   frame quota and byte quota. No frame raw pointer is exposed to actors.
2. `ActorRuntimeOptions.ActorStackBytes` and `ActorMaxStackFrames` bound
   native frame storage. Stack capacity reserves against the same runtime
   and process budgets as actor heaps. Failed second-stage reservation
   disposes the already-admitted heap and leaves no partially registered
   actor or started carrier.
3. A 128-byte root frame is charged for each actual physical handler turn.
   Nested source-lowering work can explicitly request frames through
   `ActorContext.PushFrame`; direct host callback recursion does **not**
   automatically enter those frames.
4. `ActorFrame` is a reference-identity capability tied to one exact arena,
   actor incarnation, admitted turn and carrier thread. Foreign, forged,
   stale, and off-carrier access is rejected. Frames unwind LIFO.
5. Every popped frame is cryptographically zeroed (including alignment gaps).
   Native slab memory is zeroed again on quiescent actor finalization.
6. Exceeding byte or depth limits or attempting out-of-order pop poisons the
   affected actor frame stack. `ActorRuntime` checks the poison flag even
   when guest handler code catches the quota exception, then retires
   **only** that actor. Sibling actors continue independently.
7. After `Stop` retires the actor heap, an in-flight frame can still
   unwind on the original carrier using `ValidateTurnForCleanup`, without
   admitting new allocations, heap dereferences or frame reads.
8. Finalization scrubs/releases the actor's frame slab after its dispatch
   token quiesces. A separate heap lifetime pin may postpone **heap**
   reclamation, but never justifies retaining scrubbed frame bytes.
9. Host `ActorFrameStack` objects are not serialized or sent over
   actor mailboxes. Cross-actor transport of executable functions,
   continuations or frame capabilities remains prohibited.

## Implemented tests

- `verification/Ores.Net.Tests/ActorFrameStackSelfTest.cs`: byte and depth
  bounds, frame read/write, reused-frame zeroing, strict LIFO, forged
  metadata, off-carrier pop, same-ID new-incarnation fencing, cross-actor
  denial, poison enforcement, STOP cleanup, independent budget refund and
  aggregate budget rollback.
- `ActorRuntimeSelfTest.cs`: actual actor mailbox dispatch with per-handler
  root frames, sibling survival after a caught frame quota overflow,
  termination, expected successful/faulted message accounting, and
  unsuccessful frame reservation rollback.
- `Ores.Net.ActorMemoryProbe/Program.cs`: four actor kinds under a real
  NativeAOT host probe, explicit native frame operations and stale/foreign
  frame-capability denials, plus independently accounted native heap/stack
  retention during pinned STOP.
- `Ores.Net.HttpProbe/Program.cs`: explicit per-actor native frame quotas
  maintain the 16-request bound under the HTTP stress test's 1 MiB budget.

The existing [actor-memory-native-aot workflow](../.github/workflows/actor-memory-native-aot.yml)
runs exact PR head source with .NET 10 on Linux and Windows, then publishes and
executes the real NativeAOT host memory probe. It **does not** compile an
AttoLang source actor through the `.atto` frontend into these frames.

## M2 — source-verified scalar helper bridge (experimental, November 7)

This PR now contains a **separate experimental build-time compiler-facing
project**, `compiler/Ores.Net.Compiler.ActorFrames`, which references the
existing structured frontend/typed CFG verifier and the actor-native runtime.
It does **not** alter production `oresnet compile` or admit `define actor`.

- `ActorScalarFrameProgram.Compile(source)` parses and verifies a complete
  closed, typed scalar `.atto` helper-call graph **before** execution.
  Unsupported actor/class declarations, recursive helpers, unresolved targets
  and unsupported opcodes fail closed.
- `Execute(ActorContext)` is a **nonrecursive iterative interpreter** over
  verified CFG blocks. Scalar parameter values, SSA slots, and a program-counter
  descriptor are written into real actor-owned `ActorFrame` native bytes;
  host-managed activation metadata remains separate. Each helper call
  pushes a new native frame; return unwinds it in LIFO order. No CIL actor
  method or closure lowering is claimed.
- Checked i64 arithmetic, typed bool conditions, SSA branch-edge transfers,
  loops, direct helper invocation, maximum 128 value slots/function, 32
  nested calls and finite step budgets are bounded. Source parser itself
  currently admits only **acyclic** helpers, so proper recursion/TCO remains
  unimplemented. Exceeding the interpreter step or call depth bound poisons
  the actor even when the immediate exception is caught.
- `contracts/actor-frame-v1/source-scalar-bridge.atto` is the shared source
  fixture exercised by managed actor dispatch and the Linux/Windows NativeAOT
  actor-memory probe. The NativeAOT executable interprets its source-verified
  CFG inside actor-native frames and emits an explicitly qualified
  `actor-scalar-native-frame-source` receipt. This is **source-verified
  interpreter execution inside a NativeAOT host**, **not** generated guest CIL
  or an independently verified guest binary.
- Tests added in `ActorScalarFrameProgramSelfTest` cover nested source calls,
  CFG loop/branches, checked overflow, caught execution-budget traps,
  intentionally exhausted actor-stack quota, sibling survival, rejection of
  unsupported source, and reclamation. The CI workflow now requires the
  qualified source-backed receipt from both Linux and Windows NativeAOT runs.

**Verification requirement:** source tests and compiler/project files were
committed, but none may be called *passing* until an exact-head runner has
nonzero steps and prints actual `dotnet run` and NativeAOT probe evidence.
The local environment does not contain `dotnet`. Existing
`steps=[]`/null results are runner-admission failures.

**Remaining P0:** integrate real `define actor` declaration lowering to
typed CIL/native actor handlers, then implement compiler-driven stackless
mutual recursion and proper tail-call reuse, source-owned suspension leases,
and physical process stack-overflow containment. The current scalar bridge
deliberately does **not** infer those properties.

## Remaining parity gates (priority order)

**P0 — Run exact-head CI:** restore private GitHub Actions runner admission;
prove `runner_id > 0`, nonempty executed steps, passing .NET 10 build/self
tests, Linux/Windows NativeAOT host probe, exact SHA/tree and run attempt.
Never count source-only CI or a successful compilation of host C# as guest
execution.

**P0 — Source guest entry bridge:** bind actor source invocation descriptors
to `ActorFrameStack` and typed lowered locals, including method parameters,
return PCs, nested call capture and exception cleanup. Prove at least one
compiler-produced `.atto` actor actually executes. Reject unsupported
effects/ownership cases before codegen. Preserve AttoLang native-only/AOT
policy and minimal host-code boundary.

**P0 — Stackless execution for physical failure containment:** lower all
potentially recursive guest actor calls to bounded explicit frames and an
iterative trampoline; remove dependence on unbounded CLR recursion. Require
negative tests for mutual recursion, dynamic method calls, self-recursive
`routine` rejection and proper `fnc` tail reuse. Do **not** catch
`StackOverflowException` and call it isolation. Validate that sibling actors
and carriers survive an actual *bounded guest* stack quota trap.

**P1 — Suspension/migration:** define a generation-fenced suspended
continuation lease that retains frame bytes without tying them to an OS
thread or blocking a carrier; resume by reacquiring a new physical actor
turn. Require exact once resumption, cancellation, STOP race, stale callback
and supervisor restart tests. The current `ActorFrame` token must **not**
be reused after its physical turn expires.

**P1 — Source parity:** carry ownership (`rt borrow/share/take/copy`),
TLS/Task-LS/ALS, actor groups/pub-sub, hot code upgrades, virtual actor
tenant fences, quota/fairness and structured concurrency through a shared,
canonical `.atto` conformance manifest, not independent host-only
mock implementations.

**P2 — End-to-end evidence:** run the same `.atto` fixtures and negative
mutations on Java/GraalVM, LLVM, .NET NativeAOT, Wasm and Forge/Cranelift,
recording SHA, tree, executed steps and independent review. Mark backend
parity **UNPROVEN** until compiler-backed guest receipts exist.

The initial default (64 frames, 64 KiB per actor) is a provisional admission
limit, not a guaranteed portable stack ABI. Thread stack exhaustion for
arbitrary CLR code **can still terminate the entire process**. That must
remain visible in all releases and dashboards.
