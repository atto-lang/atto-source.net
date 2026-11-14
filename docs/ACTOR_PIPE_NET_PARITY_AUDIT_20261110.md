# Actor `pipe`: .NET / LLVM / Java parity checkpoint (2026-11-10)

The actor-composition keyword `pipe` is distinct from eager functional
`|>`. AttoLang user-facing pipeline APIs belong in `.atto`; this PR only
adds a missing compiler capability gate and authored source test.

## Audited source checkpoints

- .NET `main` at `3f3606cc3ef81dad117047b4cb57609ad1a92672`:
  shipping scalar and experimental structured CIL compilers have no typed
  ActorGraph AST/IR, source actor allocation, link publication, or guest
  forwarding. Runtime ActorMailbox/ActorRuntime tests are HOST-only.
- LLVM source pipe candidate [#142](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/pull/142):
  restricted, draft two-actor i64 guest compiler/native runtime test; not a
  general multi-stage actor graph certification.
- LLVM typed pipeline candidate [#345](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/pull/345):
  restricted 2–16-stage source/transform/sink candidate; still draft.
- Java initial syntax contract [#542](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/542)
  reserves contextual infix actor `pipe`, keeping `|>` for function piping.
  Java [#2512](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/2512)
  guards unimplemented `Actor.pipe` and `Actor.connect` factories.

## Evidence gates (NOT CERTIFIED)

| Check | .NET status |
| --- | --- |
| New scalar and structured parser rejection tests | AUTHORED, NOT_EXECUTED |
| `.atto` positive two-actor fixture | AUTHORED, NOT_EXECUTED |
| Compiled actor methods and typed ActorGraph | MISSING |
| Real bounded forwarding and exactly-once admission | MISSING |
| Generation/capability fences and actor heap isolation | NOT_VERIFIED for source pipelines |
| CONTROL/supervisor lifecycle and cancellation | NOT_VERIFIED |
| Native AOT compiled guest on Linux and Windows | NOT_EXECUTED |

## Smallest implementation sequence

1. **Run this head's parser tests** on the source-local authorized .NET
   runner. Require nonzero steps, real dotnet execution, and exact-head SHA
   before claiming even the rejection gate passed.
2. Add a typed `ActorPipeExpr` / `ActorGraph` lowering plan with nominal
   ingress/output checks. Preserve contextual identifier `pipe`, reject
   pipe-after-stop, duplicate/cyclic edges and direct cross-domain sends.
3. Compile the two guest actors from `actor-pipe-two-stage.atto` through
   the actual `.atto` compiler into static CIL and Native AOT. Require
   independently audited result `6` and generation-fenced ActorRefs.
4. Bind to per-actor private arenas and supervisor-owned, bounded mailbox
   queues. Precharge graph/message/byte credits, atomically publish edges,
   roll back partial graph construction, and forbid unauthorized delivery.
5. Expand to 3+ stages with typed transforms, backpressure/Future,
   cancellation, CONTROL-only panic and deterministic stop/drain.
6. Execute the same portable fixtures on Java, LLVM and .NET, including
   failures and sanitizers where applicable; verify each exact source
   and compiled guest head independently.

No source-private cross-organization mirror is authorized by this PR.
A host-only regression or a zero-step CI job is never guest parity proof.
