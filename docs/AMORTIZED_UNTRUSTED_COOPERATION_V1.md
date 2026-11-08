# Amortized untrusted actor checkpoints — v1 (net)

## Contract

- Default slow-poll interval: **128 completed guest reductions**. The compiler's hot path uses an integer counter/increment and a predictable branch. A clock read, scheduler inspection, cancellation delivery, or runtime FFI is never performed on every iteration.
- A 512-backedge value is a *fairness handoff target*, not the hard tenant execution quota. After 512, slow polls remain at 128-edge boundaries; do not change to a slow poll on every overdue iteration. A separate safe critical-section exit may request one immediate poll.
- Replenishing a fairness slice must **never replenish hard tenant fuel**. Counter overflow saturates total accounting without disabling the independent window counter.
- Only committed backedges and compiler-verified call sites count. Untrusted source cannot mutate or bypass compiler-private counter state. Optimizer/codegen must retain checkpoint coverage; uninstrumented loop/cycle admission must fail closed.
- A slow poll may arrange resumable cooperation ONLY after the compiler proves a heap-resumable continuation, ownership-safe post-commit state, and absence of live critical/no-suspend loans. Otherwise use a defined trap/terminate path, never fake suspension of a native stack.
- Non-terminating imported/native/FFI work requires a separate isolate/process watchdog. A cooperative poll cannot make arbitrary native execution safe.

## Current backend status (2026-11-07)

The NativeAOT runtime has a test-registered 128-backedge amortization gate, but the experimental verified CFG CIL emitter has not yet injected it at backedges. No guest loop enforcement proof.

Relevant files: runtime/Ores.Net.Runtime/AmortizedCooperationQuantum.cs; verification/Ores.Net.Tests/AmortizedCooperationQuantumTests.cs.

## Missing compiler-backed gate

Inject verified backedge/recursive-call checkpoints in CIL and a real task-owned scheduler/fuel ABI; prevent critical suspension and test NativeAOT emitted guest loops.

Acceptance: a pinned exact-head test run must execute real guest loops, recursion, cancellation, critical sections, 127/128/129/511/512/513 boundaries, overflow/saturation, tenant coexistence, and negative mutations where removing a checkpoint causes admission failure. Source-free or host-only tests are not guest execution evidence; steps=null/[] is runner admission failure, not a test pass or failure.
