# .NET actor-memory consolidation decision (draft)

**One production integration candidate: `ActorArena`. No release certification.**

ActorRuntime.Spawn constructs one actor-owned `ActorArena`. Its
`ActorContext` and `ActorShardColumns` are backed by the same owner, with
persistent bytes, per-turn scratch, exact turn/carrier identity, generation-
checked capabilities, quiescent loans/pins, and a runtime plus process-native
reservation budget. The CLR still manages host metadata; there is no
separate managed GC heap per actor or zero-CLR-GC guarantee.

**Do not plug the competing prototypes directly into ActorRuntime:**
- `ActorNativeArena` (#113 → #117/#118/#124/#128/#135) explores primitive
  `ulong` handles, allocation-free hot paths and stack cleanup scopes.
- `ActorNativeHeap` (#115 → #119/#127) explores hole recycling and deferred
  native reclamation with exact leases. Its `ActorNativeHeapBudget.Process`
  remains separate from `ActorArenaBudget.Process`, so using both can
  oversubscribe the intended global native limit.

## Migration / release gates

1. Restore originating-org CI, check Actions runner/budget/permissions with
   an admin and require **real** same-head Windows x64 NativeAOT, Linux diagnostic
   and managed-test execution; zero-step jobs prove none of these.
2. Reconcile integrated branch chain #107→#109→#110→#111→#112→#120→#125
   plus sibling #130, without reapplying duplicate allocator changes.
3. Reconcile standalone layout #108 and compressed32 #114 into integrated
   #133→#134. Preserve AoS, SoA, AoSoA and SoAoS and bound geometry before
   cloning/copying metadata.
4. Only with independent review port successful primitive-handle, recycling
   and lifetime-pin algorithms behind the **single** ActorArena owner.
   Unify budget accounting, prove no early frees, no ABA and no unsafe
   cross-actor handle transfer, then measure allocations and memory retention.
5. Implement verified Oreslang ownership operations (`rt take`, `rt copy`,
   `rt share`, `rt borrow`, `rt mut borrow`) and cleanup on exceptions,
   cancellation, async continuation suspension/resumption and stop.
   `rt borrow mut` is noncanonical and must remain rejected.
6. Three global shared/private/untrusted worker domains are shared lazily.
   CONTROL is threadless; in-process untrusted actors are *not* a sandbox.

This PR's Node contract is an **advisory source-wiring guard**, not source
compiler lowering, formal verification, memory safety certification or a
security boundary.

## Reconciled behavioral proofs from #140

The consolidated architecture branch also runs the #140 cross-runtime
physical quota test: stopping an actor with an outstanding lifetime pin must
retain global native reservations until the last external lease releases,
while an unrelated actor in a different ActorRuntime refunds its own bytes.
A 24-round concurrent cancellation/retirement test races two off-carrier
pin releases and requires idempotent native-budget recovery. These tests
exercise host integration, not compiler-generated async frames. They remain
unexecuted until source-org CI is restored.

## Prototype capability migration matrix

| Candidate feature | Conditions before adoption into `ActorArena` |
| --- | --- |
| Primitive `ulong` token and lexical cleanup scope | Prove provenance and exact issuance, generated cleanup on all exits, and compiler rejection of aliased/copyable ownership. A bare numeric offset cannot authorize a guest dereference. |
| Interior free-hole recycling | Differential allocation traces, overlap/ABA/generation-rollover checks, loan-protected frees, byte scrubbing and deterministic alignment under churn. No second actor heap. |
| Async/FFI leases | Preserve exact-issued `ActorArenaLifetimePin` accounting across stop, cancellation and cross-carrier callback completion; last release triggers physical free exactly once. |
| Compressed32 offsets and SoAoS | Validate owner, incarnation, bounds, shape and live turn separately from offsets; compare logical values with AoS/SoA/AoSoA and backend golden vectors. |
| Process/tenant quota | All production actor-native allocations must use one budget ledger; CLR metadata, mailbox bytes and process RSS require separate measurement and caps. |

Additional release experiments: randomized overlapping-hole sequences, stale and
forged capabilities, maximum generation, forced allocation failure, cross-runtime
reservation pressure, physical zeroization, cancellation and actor-fault
interleavings. Neither this source guard nor the new lifetime stress tests
establish model-checked correctness or a passing NativeAOT build.

This document consolidates the design and host-budget coverage originally
developed in draft #140. That PR's independent review history remains
available, but future architecture corrections should target the integrated
#141 branch rather than creating another allocator implementation.
