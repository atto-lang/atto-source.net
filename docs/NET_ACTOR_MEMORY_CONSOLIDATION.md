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
