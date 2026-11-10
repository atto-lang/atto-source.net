# .NET actor exception/supervisor source admission — two-peer gate

**Source-authored, compiler/guest NOT_EXECUTED; NOT_CERTIFIED.**

Child of .NET actor declaration AST PR #432 (its own parent is integration #411).
New source: contracts/actor-two-peer-failure-v1/two-peer-failure-matrix.atto
Canonical conformance PR #648 Git blob:
003983eb9475d7fd680ea5b31804df02f1843f19

This is a proposed cross-backend program defining FaultWorker, Witness and the
Root supervisor. Existing .NET OresActorDeclarationParser only admits top-level
bounded actor declaration signatures with opaque bodies, int/bool parameters and
void returns. It does NOT parse/compile this full program, type any
throw/raise/panic, bind a supervisor, lower an actor handler, generate a guest
stack overflow boundary or create a NativeAOT guest.

## Compiler safety improvement in this child PR

- An actor's on_panic handler is permanently invalid (supervisor-only).
- Actor on_throw, on_raise and tear_down are reserved but fail closed until
  typed effects, actor-turn-boundary fallback scheduling and unwind exist.
  The bogus alternatives on_recover/on_throws cannot masquerade as hooks.
- Source parser AND signature-metadata builder reject these reserved names:
  callers cannot bypass parsing by constructing a forged AST.
- Strings/comments containing these spellings remain inert. Ordinary
  receive/reset metadata remains accepted. The structured shipping scalar CIL
  compiler continues to reject source-defined actor declarations.
- The ActorDeclarationAstSelfTest.Run path includes 13 source-only checks
  (6 parsed invalid signatures, 6 forged AST denials, one inert-text success).
  Marker NET_ACTOR_FAILURE_HOOK_ADMISSION_SOURCE_TESTS indicates guest=NOT_EXECUTED.
- Existing Linux/Windows actor-memory NativeAOT workflow pins the canonical
  .atto Git blob, invokes the full managed suite and requires this marker.
  These are HOST COMPILER BOUNDARY tests, not actual actor source execution.

## What must be implemented before the shared actor proof can execute

1. A single agreed portable grammar for lexical defer/recover, FailureDisposition,
   typed Error construction and supervisor-child CONTROL observation.
2. Verified actor handler AST/IR and compiler-produced NativeAOT CIL entrypoints;
   reject escape of actor ref32 and mutable guest loans across actor boundaries.
3. Compiler-generated logical guest frames with pre-native recursion budget,
   safepoints, typed spills, unwind/drop, cancellation and incarnation fencing.
4. Authenticated CONTROL failure notices with once-only hook disposition and
   supervisor visibility; panic MUST NOT call any actor-local on_panic or
   guest teardown, and stack exhaustion must not catch CLR StackOverflowException.
5. Actual source compilation and execution, first in hosted compiler and then
   Linux/Windows NativeAOT; retain per-case typed trace, 42 from a real
   surviving witness, heap-domain IDs, physical retirement, no replay,
   native artifact digests, exact PR head and nonempty runner job steps.

No unauthorized cross-organization private source mirror, no source-only
pass promotion, no merge until parent integration and independent review.
