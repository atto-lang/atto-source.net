# NativeAOT actor pipe: typed source-plan V1 checkpoint (2026-11-10)

**Status: DRAFT, NOT_EXECUTED, NOT_CERTIFIED for actor execution.**
Parent: [#440](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/440)
at `385147895dd8dc547f7481daf156de6033b1b016`.

## What this branch implements

- Adds a deliberately opt-in `oresnet plan-actor-pipe INPUT.atto` subcommand.
- Uses the existing production structured lexer and UTF-8/NUL/UTF-16/token budgets.
- Parses exactly two source-defined actors with nominal `int` / `bool`
  `on_message` inputs and outputs; rejects unknown/reused actor names.
- Reuses the verified structured CFG to check the actual source handler
  bodies (checked i64 operations, static type matching, no unchecked host
  effects).
- Validates two owned spawn bindings, a single ordered typed edge, one
  checked i64 input send, no direct consumer send, and the restricted
  producer-before-consumer terminal sequence.
- Preserves source offsets and produces a read-only graph plan.
- Exercises **compiler-generated structured scalar CIL** for the authored
  handlers via `CfgCilTestInterpreter`. The scalar oracle expects `6`
  for the target `2 → 3 → 6` example, but has **no actor dispatch**.
- Adds fail-first tests for wrong edge direction, self-edge, duplicate
  edges, type mismatch, duplicate/unknown actors, direct consumer sends,
  multiple ingress sends, invalid stop order, malformed handler types,
  accidental `|>` conflation and comment handling.

## Strict separation of verification classes

| Evidence | State |
| --- | --- |
| Parser/checked CFG and scalar handler CIL tests | AUTHORED, NOT_EXECUTED |
| Source actor AST/graph preview command | AUTHORED, NOT_EXECUTED |
| NativeAOT compiler-generated actor dispatch | MISSING |
| Per-actor private heap and owned mailbox binding | HOST INFRASTRUCTURE ONLY |
| DATA send/forward and bounded backpressure | NOT_IMPLEMENTED for guest `pipe` |
| Supervisor CONTROL, panic and cleanup | NOT_IMPLEMENTED for guest `pipe` |
| Exact-head Linux/Windows NativeAOT compiled actor guests | NOT_EXECUTED |
| Java/LLVM/.NET parity | NOT_CERTIFIED |

The opt-in command intentionally reports
`TYPED_SCALAR_ORACLE_ONLY_NOT_ACTOR_EXECUTION`, with
`executable_actor_guest=false` and `native_aot_verified=false`.
Ordinary `oresnet check`, `compile`, `check-structured` and
`compile-structured` **still reject** actor `pipe` source.
Do not make a positive guest execution claim from plan/host tests.

### Commands for an authorized source-local .NET runner

```bash
dotnet run --project verification/Ores.Net.Tests -c Release
dotnet run --project compiler/Ores.Net.Compiler -c Release -- \
  plan-actor-pipe tests/fixtures/actor-pipe-two-stage.atto
```

The plan command is an opt-in diagnostic preview, **not the production
`compile` path**. Preserve source SHA, PR head SHA, job/run IDs,
toolchain, platform, stepful logs and emitted CIL receipt.

## Why this does not yet complete actor parity

`ActorRuntime` hosts real per-actor native arenas and authenticated
`ActorMailbox` queues, but `ActorRuntime.Stop()` presently stops
a cell and `ActorMailbox.Stop()` discards pending data. It cannot
provide the LLVM-style "accepted upstream work drains into downstream
then close" contract. The compiler also does not generate actor
allocation, entry handlers, mailbox forwarding, cancellation or
supervisor bindings into static NativeAOT CIL.

Never "fix" this by executing the scalar oracle directly as the source
`pipe` guest. This would skip actor scheduling and private heaps.

## Smallest next executable slice

1. Implement explicit **drain-and-close** for a *single generation-fenced
   pipeline*, separate from unconditional abort/stop. Maintain admission
   closure, accepted vs completed credits, FIFO and scrub-on-abort.
2. Add graph-owned transactional actor allocation, edge precharge and
   rollback. Only an authenticated scheduler may forward DATA to the
   destination mailbox.
3. Emit static NativeAOT actor callbacks from the checked source
   handlers; tie each callback to one private arena and minted ActorRef.
4. Make one producer send `2`, wait for authenticated consumer completion
   `6` (not just the existing `main() = 0`), drain, and retire.
5. Run exact-head Linux and Windows native guests with negative
   capacity, stale-reference, wrong-type, duplicate-edge, aborted-
   startup, panic-to-supervisor and stop-race cases.

No unapproved cross-org private-source Git-object copying is authorized.
A zero-step Actions failure is **runner admission failure**, not test execution.
