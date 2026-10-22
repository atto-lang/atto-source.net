# ADR: Select one integrated .NET actor-memory runtime

**Status:** proposed production architecture; all associated PRs remain draft.
**Review scope:** .NET compiler/runtime actor-memory parity, October 2026.

## Decision

Use `ActorArena` (the allocator currently instantiated by `ActorRuntime.Spawn`)
as the **sole production-candidate actor-memory substrate** for the integrated
.NET actor runtime. Its per-actor aligned persistent and scratch regions,
turn/carrier fencing, exact issued capabilities, read loans, continuation
lifetime pins, runtime and process budgets, and quiescent physical reclamation
form the safety contract. This choice does **not** disable the CLR GC: all
managed runtime metadata, control paths and host objects remain collectible.

`ActorNativeArena` (#113, #117, #118, #124, #128, #135) and
`ActorNativeHeap` (#115, #119, #127) are **experimental prototypes**, not
competing production configuration options. Do not wire them into
`ActorRuntime` or expose guest-facing ABI selections that depend on one
prototype until the features below have been audited and reconciled.

No separate CLR-managed GC heap per actor; no per-actor OS thread pools.
Actor turns execute within the runtime's three lazy global worker domains
(Private, Shared, Untrusted) and CONTROL remains without a carrier pool.
Untrusted execution on a CLR thread is not an OS sandbox.

## Single authoritative ownership and budget

1. Actor code can dereference only within a live, exact, admitted physical turn
   on the same carrier. Continuations release that turn before suspension,
   preserve a lifetime-only hold, and resume by admitting a new physical turn.
2. Byte ownership and issued capabilities belong to a single logical actor
   identity/incarnation. Stale, foreign, forged or double-released handles fail
   closed. Borrowed bytes cannot be dropped/mutated until relevant loans end.
3. Physical zeroization and native freeing are gated by active turns, read
   loans and outstanding lifetime pins, including cancelled I/O and FFI.
4. Each actor's reserved native capacity is charged to its runtime quota and
   `ActorArenaBudget.Process`, and remains charged after logical retirement
   until physical reclamation. Do not sum unrelated experimental-budget
   counters and call that a process-global quota: they do not share a ledger.
5. Each store has exactly one authoritative physical layout (AoS, SoA,
   AoSoA or grouped SoAoS), with ownership proofs independent of layout.
   A compressed32 word is an offset representation, **not** an owning pointer,
   actor reference or authority token.

## Reconciliation decisions

| Prototype capability | Production migration requirement |
| --- | --- |
| `ActorNativeArena` primitive `ulong` tokens + lexical `CleanupScope` | Evaluate host allocation pressure and provenance; build compiler-generated cleanup on all exits. Primitive offset tokens alone are not forge-resistant guest capabilities. |
| `ActorNativeHeap` coalesced interior-hole reuse | Port the allocator strategy into `ActorArena` only after ABA, loan, retired-turn, overlap and zeroization proof; do not enable a second runtime heap. |
| `ActorNativeHeap` async/FFI lease policy | Preserve `ActorArenaLifetimePin` exact issuance, boundedness and quiescence semantics; do not interoperate raw lease objects between the two heaps. |
| SoAoS + compressed32 integration | Keep offsets unforgeable via owner/region/type/generation and live-turn checks; enforce metadata and span bounds before allocating host buffers. |
| Runtime/tenant/process reservations | Enforce one unified `ActorArenaBudget.Process` ledger for production actors. Experimental prototype quotas must not be represented as fully global. |

## Proposed merge and release gates

- Merge stack foundations only with approved code review and **same-head
  executable CI steps** for managed .NET 10, Windows Native AOT, Linux
  diagnostics, correctness tests and native executable output. A zero-step
  failed job proves none of these things.
- Pin expected SHA/tree and evaluate originating-repo run ID, job conclusion,
  observed step execution and receipt; a JSON receipt alone is not a signature.
- Prove across at least two independent runtimes that process reservations
  grow by both actors' aligned capacities, logical stop keeps pinned native
  memory charged, and the last off-carrier lease refunds capacity exactly once
  (covered by `CrossRuntimeNativeCapacityRemainsChargedUntilQuiescence`).
- Add allocator-level property/stress tests for overlapping holes, maximum
  generation rollover, failure atomicity, forged capability rejection, burst
  allocation, cancellation and FFI callback termination.
- Extend compiler lowering for `rt take`, `rt copy`, `rt share`, `rt borrow`
  and `rt mut borrow`; enforce cleanup across return, exception, cancellation
  and supervision. Fail closed for unsupported source features.
- Validate runtime AoS/SoA/AoSoA/SoAoS logical equality; later compare
  LLVM, GraalVM, JS and WASM semantic conformance without demanding identical
  physical heaps across backends.

## Nonclaims

This ADR does not certify zero-GC .NET, full Oreslang guest-heap lowering,
untrusted-code isolation, Native AOT passing builds, or whole-process RSS.
These remain separately measurable release requirements.
