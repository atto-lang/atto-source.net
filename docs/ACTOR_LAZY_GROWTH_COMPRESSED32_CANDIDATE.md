# Actor-native on-demand memory and compressed32 boundary (candidate)

This draft changes the **host-only** `ActorNativeArena` primitive used by the
.NET runtime; it does not claim source-level `.atto` class/actor-object
lowering or NativeAOT execution.

- The constructor sets a logical per-actor maximum (currently 64 MiB) without
  allocating the maximum block. The first admitted object allocates a small
  64-byte-aligned physical block; later allocations geometrically grow backing.
- Growth charges the *physical* delta to both a supplied tenant budget and the
  process budget before native allocation. Rejection cannot publish a partial
  new region. Physical zeroization/free precedes quota refund.
- Live objects keep opaque generation-checked handles and **logical byte
  offsets** across backing relocation. A native host pointer is never exposed
  through this class; growth under the arena lock excludes concurrent access.
  The backing may only move while this no-raw-pointer-escape invariant holds.
- Newly acquired bytes are zeroed, and superseded backing is scrubbed before
  release. Actor retirement still requires caller-enforced quiescence; this
  primitive alone is not an actor-turn, loan, or FFI isolation gate.
- Actor-private reference *storage* remains **unsigned 32-bit actor-relative**
  words in the candidate compressed32 contract, not 64-bit heap pointers. The
  native `byte*` base, CLR object handles, FFI/device pointers, and opaque
  `ulong` generation tokens are **not** guest reference fields. A 32-bit word
  contains no actor/incarnation/generation/borrow authority.
- The encoded reference convention is `0=null` and
  `word=byte_offset/8+1` for aligned object starts; 4-byte fields store the
  word. The representable *logical* offset limit is 32 GiB minus 8 bytes,
  **not** a promise that an actor can allocate that much. Runtime physical
  quotas are smaller and independently enforced. Do not silently widen an
  AttoLang managed-reference field to 8 bytes.
- The on-demand profile does not replace deterministic real-time actors:
  `realtime-prefault` actors must establish resident backing before dispatch
  and reject growth during time-critical turns.

## Verification

`ActorNativeArenaSelfTest` now checks no eager allocation, growth with an
unchanged live root, independent actors, odd quota/alignment, over-quota
failure neutrality, aggregate tenant/process admission, zeroing and refund
after physical reclaim. **.NET compiler, these .NET host tests, NativeAOT and
compiled AttoLang guest tests have not been executed in this environment.**

Follow-up work must bind the runtime's compiled actor-object lowering to an
authenticated arena/turn, implement checked compact32 field loads/stores,
prevent raw refs crossing mailbox/FFI, test carrier migration, and execute
identical `.atto` source on Java/GraalVM and LLVM. Conformance authority:
oreslang-platform-conformance#157 and LLVM#1330. Do not promote this
source-only draft from host substrate to guest parity on zero-step CI.
