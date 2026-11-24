# Bounded actor pipe source plan and compilation

## Source profile

- Adds a deliberately opt-in `oresnet plan-actor-pipe INPUT.atto` subcommand.
- Uses the existing production structured lexer and UTF-8/NUL/UTF-16/token budgets.
- Parses up to sixteen independently owned actor instances with nominal `int` / `bool`
  `on_message` inputs and outputs; rejects unknown types and reused bindings.
- Reuses the verified structured CFG to check the actual source handler
  bodies (checked i64 operations, static type matching, no unchecked host
  effects).
- Validates owned spawn bindings, one ordered chain of typed edges, one
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

## Compiler entrypoints

`plan-actor-pipe` reports `TYPED_SCALAR_ORACLE_ONLY_NOT_ACTOR_EXECUTION` with
`executable_actor_guest=false` and `native_aot_verified=false`. Use it to inspect
direction, message ports and source offsets. `pipe!` and `rpipe!` are aliases
for `pipe` and `rpipe` in this plan. Same-direction edges may use either
spelling. Mixed directions and malformed adjacent suffixes are rejected.

`compile-actor-pipe INPUT.atto --out /absolute/path/OresGenerated.dll` emits
checked CIL for each guest handler and a statically linked stage dispatch ABI.
`Ores.Net.ActorPipeNativeHost` links the artifact and registers one private
actor per stage. Each stage forwards its computed value through the next
actor's single-message mailbox. The host drains from source to sink and
reports the final value after every actor and native arena has retired. A
stage fault fails the run without emitting a value. The compiler rejects
cyclic handler control flow so an admitted turn can settle.

The opt-in compiler profile covers a fixed owned chain, one input, synchronous
`on_message` handlers and generation-bound graceful retirement. Its ABI
represents int and bool ports as checked 64-bit values. The CLR host uses
managed scheduler objects and delegates, so this profile carries no
zero-GC claim. General actor source, suspendable handlers, multi-message
backpressure, streams, and supervisor CONTROL are outside this profile.
Functional `|>` and `<|` use separate admission rules.

Run the CoreCLR verification and the standalone NativeAOT verification for
your native target. The script checks guest results, actual turn counts,
retirement, fault settlement, compilation failures and native artifact layout.

```bash
bash verification/assert-actor-pipe-guest.sh
bash verification/assert-actor-pipe-guest.sh osx-arm64
```
