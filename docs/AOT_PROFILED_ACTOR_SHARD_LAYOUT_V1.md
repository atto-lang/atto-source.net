# AOT profile-guided actor shard layout — host substrate v1

## Status and boundaries

**DRAFT / HOST-ONLY / NOT_EXECUTED for AttoLang guest parity.** This slice
adds an independent .NET Native AOT-compatible decision mechanism to the
existing actor-owned `ActorShardColumns` substrate. It does **not** add
`define inverted class` syntax, typed compiler lowering, cross-backend
conformance, a real CLR JIT, dynamic CIL, or live shard conversion. The
existing compiler must continue to reject unsupported source constructs.

Production remains fully Native AOT. A scheduler may record bounded hot
access patterns and select one of the **already compiled** layouts when
allocating the next shard. The live shard retains its single authoritative
physical layout for its entire lifetime. No runtime-generated code or dual
AoS/SoA replicas are introduced.

## Trusted-host contract

`ActorShardLayoutAdvisorV1` is internal to the runtime assembly and must
only be reached through a supervisor/scheduler-authenticated actor turn.
The advisor is constructed with the exact owning `ActorArena` and
an issued `ActorArenaTurn`. Every observation, recommendation and retirement
revalidates that turn's **object identity, opaque authority, incarnation,
epoch, live state and admitted carrier thread** using the arena's internal
gate. Matching numeric actor IDs alone never authorize access.
The profiler assumes a single serial writer and cannot be shared between
concurrent actors; it must be retired with its owner. Exposing it as a
guest FFI is forbidden. A host call to `EnterTurn` remains a trusted
scheduler admission boundary, not proof that arbitrary guest code may mint
an actor turn.

- One up-front managed advisor object; `Observe` uses only bounded integer
  counters and no clock, reflection, dynamic compilation, I/O, callbacks,
  or per-sample managed allocations.
- Sample ceiling is immutable (64–65,536, default 4,096); count addition
  saturates without integer overflow, even when a batch is larger than
  remaining capacity.
- Cold profiles and random-heavy workloads choose AoS.
- A two-to-one majority of at least 64 column samples chooses SoA.
- A two-to-one majority of at least 64 row samples chooses AoSoA only for
  at least two fields and two full preferred tiles.
- AoSoA lanes are powers of two, 2–1,024; padding is checked against the
  existing 16 MiB native shard quota. Otherwise fall back to AoS.
- Invalid shape, lane size, foreign/forged/stale or off-carrier turn,
  retired advisor and allocation above the quota fail closed, without
  allocating a shard.
- Retirement clears all counters and rejects subsequent operations.

The ratios are conservative **heuristics**, not a claim of measured
speedups. The decision record has `ActorDataLayout`, lanes and sample
count. The host passes those values to the existing `ActorShardColumns`
constructor; it never reinterprets a live allocation or invalidates an
existing borrow by changing its physical layout.

## Testing and promotion

The existing `verification/Ores.Net.Tests` executable now calls
`ActorShardLayoutAdvisorV1SelfTest`. Tests cover cold/saturated counters,
shape/lanes, access-locality policies, stale incarnation, retirement,
steady-state sampling allocation deltas, wrong owner with matching numeric
identity, stale turn epoch, wrong carrier thread, and actual actor-owned
unmanaged AoS/SoA/AoSoA shard checksums and exact quota refund.

Run on a machine with the .NET 10 SDK:

```sh
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
```

Before describing this as **verified Native AOT**, also publish and
execute the tests/probe on the actual linux-x64 and win-x64 Native AOT
targets, with exact source head and tree provenance. For **source parity**
add genuine `.atto` positive and negative compiler/guest tests to .NET,
Java and LLVM and compare identical source blobs and expected outcomes.
A host-only test cannot certify guest source semantics.

## Relation to tracing and zero-GC

`NativeTraceInstrumentationV1` already provides NativeAOT-safe static
site policy and versioned executable leases. This new layout selection
is separate from dynamically generated functions: the compiler may
eventually provide profile feedback and precompiled specialization tables.
No CLR JIT, `Reflection.Emit`, or runtime assembly load is required.

`ActorShardColumns` stores its payload in unmanaged actor-owned arena
memory. The advisor avoids per-observation CLR heap allocation, but
scheduler, host, metadata and test objects may still allocate. **Do not
claim a fully zero-GC .NET process.** A zero-allocation guest hot path must
be established independently via real compiled guest execution and
collection/allocation counters under Native AOT.
