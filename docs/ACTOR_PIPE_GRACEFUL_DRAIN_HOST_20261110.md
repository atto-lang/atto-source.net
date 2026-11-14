# Actor pipeline drain-and-close: host-only .NET NativeAOT substrate (2026-11-10)

**DRAFT / NOT_EXECUTED / NOT_CERTIFIED for AttoLang actor `pipe`.**

Stacked on [.NET #443](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/443),
which is itself stacked on [#440](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/440).
The new opt-in source graph planner and checked CIL scalar oracle do not yet
connect to this runtime implementation.

## Contract

The previous `ActorRuntime.Stop(ActorRef)` is an **abort**:
it rejects future sends and erases queued, not-yet-handled mailbox DATA.

New `Task ActorRuntime.DrainAndStop(ActorRef)` is different:

1. Authenticate the exact actor generation + non-exportable nonce.
2. Close new DATA admission under the existing per-cell gate.
3. Preserve all previously admitted queued DATA and the active turn.
4. Drive queued work through existing bounded scheduler tokens.
5. Once the final turn exits and the mailbox becomes empty,
   retire the actor and return its existing `Terminated` task receipt.
6. Repeated calls during drain yield the same receipt. A later explicit
   `Stop` may still abort, scrubbing queued data and retiring the actor.
7. If scheduling cannot start an idle queued actor, roll back the
   admission seal rather than silently strand prior accepted DATA.
8. Actor handler failure remains abortive and does not claim graceful success.

The completion task does NOT currently distinguish clean drain from fault
or forced abort; verified terminal result discrimination remains required
before guest `get_done()` or full supervisor semantics can be exposed.

## Host-only regression coverage (authored, NOT_EXECUTED)

- Two accepted DATA envelopes, first active turn artificially held while
  the second waits; seal admission; deny a third; process both before completion
- Stable, generation-bound terminal receipt across repeated calls
- Two real host actor cells with private arenas and scheduler-owned mailboxes:
  send `2` to producer, forward `3`, consumer observes `6`; wait for
  producer completion *before* sealing the sink
- Forged same-ID ActorRef must not close someone else's mailbox
- Intentional `Stop` after graceful seal must still erase pending DATA
- End-state outstanding native arena reservation must be zero

These are C# runtime/host tests, not generated `.atto` actor guest execution.

## Required compiler/runtime bridge

To promote this to real actor `pipe` parity:

- Compiler-generated static handler dispatch, private heap and trusted
  typed `ActorRef` descriptors from `.atto` source
- Authenticated graph publication and transactional rollback
- Mailbox credit reservation *across* the producer→consumer forwarding edge;
  a successful producer send must never make a later downstream send fail
  due to capacity
- Generation-correct per-flow completion and supervisor CONTROL notices,
  distinguishing success, failure and explicit abort
- Source `stop producer; stop consumer;` must compile to asynchronous
  ordered barriers rather than eagerly aborting both mailboxes
- Exact-head executed Linux/Windows Native AOT guest tests, along with
  fault injection and cross-backend LLVM/Java conformance

## Verification command

```bash
dotnet run --project verification/Ores.Net.Tests -c Release
```

Require real nonzero-step source-authorized CI, exact commit identity, logs
and test artifacts. The source org's preceding 2026-11-10 Actions failures
had no executed steps. Do not copy private Git trees to unapproved orgs
as a runner workaround.
