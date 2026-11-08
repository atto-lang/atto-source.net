# Demand-backed native actor heaps in actual .NET actor dispatch

**Status: implementation draft / not yet compiler-backed guest parity.**

The integrated .NET `ActorRuntime` now has an **opt-in** non-realtime
`ActorRuntimeOptions.DemandBackedActorHeaps` profile. The existing eager
aligned allocation profile remains the default pending executed NativeAOT and
compiled `.atto` evidence. This affects the actor-owned `ActorArena`
actually used by `ActorRuntime.Spawn`; it is separate from `ActorNativeArena`
in independent draft PR #412.

## Allocation contract

Each actor/incarnation gets a distinct `ActorArena` with stable logical
allocation offsets and generation-fenced issued capabilities. A logical
`ActorPersistentBytes`/`ActorScratchBytes` maximum is **not** a native
memory commitment. In demand-backed mode the actor starts with no native
payload committed. Persistent and scratch regions grow independently in bounded
64-byte-aligned blocks (initial growth at most 4 KiB). This preserves the
existing actor's root bytes across growth, including when a read-only loan is
live. Growth is **denied while any external lifetime/FFI pin is live**, since
an integration may otherwise retain an undisclosed physical pointer. All
operations and slab replacements are serialized under the arena gate. No
pointer or native-backed span escapes through this arena API.

Before new native backing is installed, its *delta* is reserved against the
runtime/tenant and process budgets. Failure to reserve or allocate is neutral:
existing backing, published slot identities and charges remain unchanged.
Replacement memory is zeroed; superseded memory is scrubbed before physical
free. Physical shutdown waits for the actor turn, immutable read loans and
continuation/FFI pins to quiesce. Quota is refunded only after scrub/free.
`ActorRuntime.NativeReservedBytes` now reflects **physically allocated
backing**, which can be zero for actors that have not yet allocated any bytes.

The actor-owned logical frame stack is still stored in the same persistent
arena. Its first push may trigger native allocation; stack quota and frame
bounds remain independently enforced.

## 32-bit guest reference contract

AttoLang class/record/array **guest heap reference fields must be unsigned
32-bit actor-relative words**, not C# native pointers, .NET object references
or the opaque generation-bearing `ActorArenaHandle` class itself. Candidate
ABI from platform-conformance #157 and #606: `0 = null`, otherwise
`word = aligned_object_start_offset/8 + 1`, 4-byte stored field.
Actor identity, incarnation, generation, publication, liveness, current
turn and borrow authority are checked out of line, including after carrier
migration. The 32-bit word alone is never an authority or wire value.

The new demand-backed host allocation code **does not yet implement** checked
source-generated `.atto` graph field loads/stores. Compiled guest integration
is a separate acceptance gate, tracked by .NET #413.

## Immutable containers and critical sections

Immutability by itself allows safe concurrent **reads** of a *transitively
frozen and correctly published* snapshot. It does not make a mutable root
reference safe, and it never allows borrowing an actor-local arena across an
actor or tenant boundary. A shared snapshot requires a separately typed,
Sendable shared-region authority with lifetime ownership, not ref32 copying.

To publish a new immutable version, compute the candidate outside
`critical`, then use a non-suspending `critical` block to validate the
expected root revision and atomically swap the root. A conflict retries from
the new immutable snapshot. Do not suspend/await, perform FFI or I/O, or hold
the critical section across allocation backpressure or actor scheduling.
`critical` is not a substitute for deep immutability or safe reclamation.
See cross-backend conformance draft (pending) for the candidate source and
negative fixtures. Existing compiler `critical` implementations are not
yet verified across GraalVM, LLVM and .NET NativeAOT.

## Verification boundaries

New `ActorArenaSelfTest` and `ActorRuntimeSelfTest` cases exercise the
real runtime dispatch, growth under a read loan, two equal-offset private
heaps, aggregate-denied allocation, failure neutrality, scratch epoch reset,
pinned retirement, and quota return before reusing memory. These are host
tests, not guest execution. **No `dotnet` SDK is installed in this session,
and GitHub Actions may fail with `steps=null`. Thus .NET host tests,
NativeAOT and guest `.atto` parity are NOT_EXECUTED until proven at the exact
PR head.** Never change status to PASS from a source-free oracle or zero-step CI.

## Security / performance limitations

Dynamic slab relocation requires a proof that there are no escaped raw native
pointers. Separate FFI shared-memory/pinned-lease APIs must pin or deny growth.
No real-time guarantee is implied by dynamically expanding backing.
This is not an operating-system sandbox, tracing-collector replacement,
or claim of a globally GC-free CLR host.
