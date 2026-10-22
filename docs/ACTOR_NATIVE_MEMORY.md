# Actor-owned native heaps: .NET substrate and parity gate

The Oreslang .NET frontend currently accepts only a checked-i64 subset; this
document specifies the new host-level actor memory *substrate*, not a claim that
Oreslang classes, closures, continuations or collections are already lowered
onto it. Full actor syntax and ownership lowering remain unsupported.

## Physical allocation and managed-GC scope

`ActorNativeHeap` owns one bounded, zero-initialized `NativeMemory` allocation per
actor incarnation. The storage is unmanaged; the CLR does not trace guest
object bytes stored here. Each actor has an independent native allocation
and independent byte/slot quotas. This is **not** a separate CLR GC heap,
operating-system process or security sandbox.

Managed footprint per actor: one heap/control object, fixed slot and free-range
tables, one synchronization object, and one opaque Guid. No managed object is
created for a guest allocation, read, write, remove, or recycled range.
Cross-turn lifetime leases currently allocate one small managed reference-type
object per outstanding asynchronous/FFI lifetime. Worker, mailbox, compiler
and source-value allocations still participate in CLR GC. A near-zero-GC
performance claim requires allocation profiling of compiled source workloads.

This heap supports stable non-moving locations, bounds-checked copying, fixed
metadata, free-range coalescing, 64-byte maximum alignment, zeroing on removal,
and full zeroization on retirement. Reuse of holes prevents a persistent actor
field from forcing every short-lived message allocation to consume fresh bump
space. Generation-checked handles reject slot reuse (ABA), foreign actors
and foreign heaps. Local handles have no JSON wire representation.

## Ownership and quiescence

1. Compiler proves affine ownership and exclusive mutable borrows. The
   canonical mutable borrow syntax is `rt mut borrow`, not `rt borrow mut`.
2. Generated drop glue removes message-local objects on all exits, including
   exceptions, cancellations and early returns; persistent fields survive turns.
3. Scheduler acquires a lifetime lease before an await, callback, pinned native
   operation, or FFI reference is allowed to outlive the active actor turn.
   The lease is idempotently released after the operation has *actually*
   quiesced, including error and cancellation paths.
4. Supervisor closes ingress and detaches the actor from execution, then
   requests heap retirement. Retirement seals new accesses and leases
   immediately. Physical native free is deferred until leases reach zero.
   Untracked raw pointers are never permitted to escape the runtime API.
5. Heap and capability are *incarnation-specific*. Restart creates a new
   actor reference and heap; source-level references and old handles cannot
   authorize the new incarnation.
6. Unreleased leases quarantine native bytes rather than freeing them
   unsafely. A trusted supervisor must diagnose or terminate the owning
   isolation domain; timeout alone is not proof of safety.

A public native heap API cannot by itself prove that every source-language
borrow or continuation is tracked. Strict no-GC and hard-real-time profiles
**must fail closed** until those compiler and scheduler proofs exist.

## Explicit immutable borrowed access (prototype)

An actor can now call `AcquireReadOnlyLoan(actor, handle)` to pin a single
generation-checked allocation. While that loan exists, checked writes and
deletion of **that allocation** fail closed; unrelated allocations remain
mutable. The loan can copy bytes into a caller-owned destination and can drain
during actor retirement. The last loan releases its native lifetime pin so a
previously requested retirement may physically reclaim bytes and release the
aggregate memory budget.

Acquiring and releasing a loan allocates one small managed reference-type
guard per outstanding borrow; ordinary payload reads/writes remain
non-allocating. Guest callers must not manufacture `ActorRef` authority or
export borrowed native memory as raw pointers. The borrow checker and task
scheduler still need to generate/track these loans across `await`, native
callbacks, failure unwind and cancellation. A manually acquired loan does
**not** prove that all source-language mutable aliases were rejected.

## Stack-only deterministic temporary object ownership

`ActorNativeHeap.AllocateScoped(owner, size, alignment)` returns a C#
`ref struct OwnedAllocation` rather than allocating a managed wrapper
on the CLR heap. Generated synchronous `using`/`try-finally` code can
reclaim and zero native temporary slots on both normal and exception
exits, even while older actor fields remain alive. Scope read/write
paths remain actor-authorized and generation-checked.

The `ref struct` restriction forbids ordinary managed boxing and
capturing in heap-resident delegates, but C# `ref struct` values are
**copyable**, not linear capabilities. A duplicate may attempt a second
drop; the underlying native heap rejects it through the slot generation
check. Source-level Oreslang `rt take` and borrow checking must still
prove a single owner and reject prohibited captures/suspensions. Failed
drop due to an outstanding native read loan throws instead of silently
freeing borrowed storage. The runtime supervisor must still complete
lease draining before actor retirement.

This is a no-wrapper allocation **for the temporary scope**, not a
claim that the rest of the compiler and runtime generate no CLR GC work.
Long-lived async frames belong in a separately proven persistent actor
heap/continuation arena.

## Remaining integration and verification gates

- Lower source actor object fields, collections, text, closures, async frames,
  and generated drop glue to native storage.
- Couple lifetime-lease acquisition to task/FFI completion, actor restart,
  cancellation and physical scheduler quiescence.
- Add a trusted, nonforgeable compiler provenance witness before allowing a
  zero-*guest-tracing* profile. The CLR still controls its own GC.
- Run the managed self-test suite, Windows Native AOT publish/execution,
  concurrent stop/lease stress, sanitizer-backed native boundary tests, and
  allocation profiling on *the exact PR head*. Hosted run admission alone
  does not count as passed tests.
- Ensure no cross-organization private source mirroring occurs without an
  explicit, approved export policy. Using another organization's Actions
  minutes is not itself source-sharing authorization.

## Backend targets

| Backend | Actor-owned payload | GC semantics |
| --- | --- | --- |
| LLVM | Independent native heap/arena with compiler-owned drops | No tracing GC for native guest values, once lowering and quiescence are proven |
| GraalVM / Java | Actor-owned off-heap regions, generated checked accessors, static drops | JVM control-plane GC persists; reduce guest allocations through FFM/Native Image |
| .NET | This per-actor unmanaged heap, future generated CIL accesses | CLR control-plane GC persists; verify guest allocation elimination empirically |

No per-actor Graal isolate, per-actor CLR GC heap, or per-actor OS worker
pool is required. Actor memory regions remain independent even when the
runtime shares execution carriers and one managed GC domain.
