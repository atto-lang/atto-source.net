# Actor pipeline v3 — Java / LLVM / .NET source parity gate

Status: **shared semantic fixtures, not proof of guest execution**.

Each backend must compile and run `double-42.atto` and `int-to-bool.atto`
with true actor instance isolation, protected message admission, bounded
FIFO queues, and source-bound `emit(expr)`. All three targets must reject
`reject-inconsistent-emits.atto` **before** creating executable IR.

- `pipe` and `|` are actor composition aliases only for static
  `spawn Actor()` stages; `|>` remains a functional value pipeline.
- Stage count is 2..16, nominal actor types may repeat but **instance
  identities** cannot. Supervisors cannot become DATA stages.
- Real type inference must collect emitted expression types from every
  receive control-flow path. Input `ActorMail<T>` does not imply output `T`.
- Adjacent output/input types match exactly: `int` and `bool` only for v3.
  Nonterminal stages must emit a declared type; terminal sinks emit nothing.
- Supervisor-owned topology is published atomically, invalid links and
  cycles fail closed, actor-origin messages commit only after a successful
  turn. Preserve actor-owned heaps, own-stack/single-carrier constraints,
  CONTROL-only panic and lifetime/generation fencing.
- Buffer overflow, actor failure and cancellation must never silently
  drop *accepted* mail. No pre-AOT dynamic source evaluation.

**Evidence levels:** (A) actual source-compiled actor guest execution;
(B) authenticated native mail routing without guest compilation; (C) typed
static compiler contract only; (D) source syntax rejected fail-closed.

New `ActorEmitPipelineContractV3`, `ActorEmitPipeContractV3` and
`ActorEmitPipeContractV3` implementations are level **C**. They are
deliberately independent of the legacy explicit PipeRole/DAG contracts.
Do not turn a passing host contract into an A-level guest claim.

When native compilers support source lowering, record separately for each
backend: exact source tree SHA; exact fixture blobs; parser/typecheck
positive and negative results; source-compiled guest output, actor heap
isolation, bounded FIFO and teardown; Java GraalVM JIT/Native Image,
LLVM O0/O2 ASan/UBSan/native AOT, and .NET CIL hosted + Linux/Windows
Native AOT. Unrun checks are **NOT EXECUTED**.
