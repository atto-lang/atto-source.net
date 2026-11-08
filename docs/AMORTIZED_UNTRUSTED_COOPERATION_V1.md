# Amortized untrusted actor checkpoints — v1 (net)

## Contract

- Default slow-poll interval: **128 completed guest reductions**. The instrumented hot path uses a small integer countdown and a predictable branch; 64-bit aggregate accounting occurs only on the 128th reduction. A clock read, scheduler inspection, cancellation delivery, or runtime FFI is never performed on every iteration.
- A 512-backedge value is a *fairness handoff target*, not the hard tenant execution quota. After 512, slow polls remain at 128-edge boundaries; do not change to a slow poll on every overdue iteration. A separate safe critical-section exit may request one immediate poll.
- Replenishing a fairness slice must **never replenish hard tenant fuel**. Counter overflow saturates total accounting without disabling the independent window counter.
- Only committed backedges and compiler-verified call sites count. Untrusted source cannot mutate or bypass compiler-private counter state. Optimizer/codegen must retain checkpoint coverage; uninstrumented loop/cycle admission must fail closed.
- A slow poll may arrange resumable cooperation ONLY after the compiler proves a heap-resumable continuation, ownership-safe post-commit state, and absence of live critical/no-suspend loans. Otherwise use a defined trap/terminate path, never fake suspension of a native stack.
- Non-terminating imported/native/FFI work requires a separate isolate/process watchdog. A cooperative poll cannot make arbitrary native execution safe.

## Current backend status (2026-11-07)

NativeAOT host cooperation uses a 128-tick countdown with million-tick host tests and sibling isolation. The opt-in `EmitLoopFreeUntrustedPrototypeLibrary` still admits only an acyclic, call-free, conservatively weighted CFG; loop/call/oversize graphs fail before DLL publication. An additional **opt-in trap-only compiler-inserted CIL prototype** now instruments every verified CFG block entry, including loops. It runs neither actor suspension nor hard tenant-incarnation fuel; this is not a production untrusted execution or OS-sandbox proof.

## Missing compiler-backed gate

Inject verified backedge/recursive-call checkpoints in CIL and a real task-owned scheduler/fuel ABI; prevent critical suspension and test NativeAOT emitted guest loops.

Acceptance: a pinned exact-head test run must execute real guest loops, recursion, cancellation, critical sections, 127/128/129/511/512/513 boundaries, overflow/saturation, tenant coexistence, and negative mutations where removing a checkpoint causes admission failure. Source-free or host-only tests are not guest execution evidence; steps=null/[] is runner admission failure, not a test pass or failure.


## Opt-in verified CFG → metered CIL prototype

`CilEmitter.EmitMeteredUntrustedPrototypeLibrary` is a new, explicit
**trap-only** path for one parameterless `i64` function. It accepts a
compiler-verified CFG with loops, but rejects every helper call and graph
whose conservative static emitted-CIL estimate exceeds 4096 operations.
The earlier `EmitLoopFreeUntrustedPrototypeLibrary` remains unchanged:
cycles and calls are rejected before it writes a file.

For each verified CFG block, the CIL emitter inserts a private `i64`
countdown starting at 128 and a private per-**invocation** hard budget
starting at 4096. Each block entry executes a subtraction and branch;
only every 128th block entry performs an additional hard-budget decrement
and conditional terminal `throw null` (CLR `NullReferenceException`).
No clock, scheduler or external runtime callback executes at each entry.
The checkpoint cannot be skipped by a CFG cycle: all branches target
compiler-emitted basic-block headers. Compiled guest code cannot address
the two appended private local slots.

**Scope is narrower than generic AttoLang checkpoints:** the unit is a
verified *CFG block visit*, not a source reduction/backedge; the quota is
per method call, NOT a nonrenewable tenant incarnation quota; suspension,
continuations, cancellation and `critical` are not implemented. The
trap is not a cooperative yield. No production NativeAOT or hostile actor
admission must use this experimental entry until a trusted tenant hard
budget, scheduler-safe heap continuation, and post-optimization proof
exist.

`UntrustedMeteredCfgTests` executes real source→verified CFG→CIL using
the independent Cecil-based verifier/interpreter: positive iterations
1/127/128/129/511/512/513, an intentionally excessive 100000-iteration
loop that must trap before returning, call/recursion denial, existing
acyclic-entry denial, exact block guard-site counts and deterministic PE
output. A passing hosted workflow needs **nonzero executed steps**;
merely committing tests or observing an empty-steps CI failure is not
compiler execution evidence.
