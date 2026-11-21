# Java/GraalVM ↔ .NET NativeAOT actor-heap admission parity V1

Status: **IMPLEMENTED AS SOURCE / CLR AND NATIVEAOT EXECUTION NOT VERIFIED**.
This is a narrow *host runtime policy* slice, not proof of compiler-lowered
guest objects or five-backend parity.

## Exact Java reference

Reference repo: `ores-truffle-oreslang/oreslang-source.java`  
Observed Java `main`: `a196bc7b46bce9bde73cde022fc0a736a8017e62`  
Reference file:
`src/main/java/dev/oreslang/runtime/ActorHeapAdmission.java`  
Exact Git blob: `89df0108c04a4ac9236e498a50a03b236715f8c7`

Java `ActorOwnedGraphArena.java` is a separate, host-managed graph
implementation, not a native guest heap; its observed Git blob is
`80fc7aa2a17a28ef6670494a5006514daa1e36e8`. Its graph links do
not own their targets and cycles cannot independently prevent actor
memory retirement. This description is *not* a bit-for-bit ABI mapping.

## .NET implementation

- `runtime/Ores.Net.Runtime/ActorHeapAdmissionV1.cs` implements
  Java-compatible policy acceptance and denial. Accepted GC modes:
  `hybrid`, `ownership-first`. Accepted isolation mode:
  `trusted-inprocess`. Names are ordinal/culture-invariant, case-
  insensitive, and whitespace-trimmed.
- Rejected GC modes: `strict-no-gc`, `zero-gc`, `manual-only`.
  Rejected isolation modes: `vm-isolate`,
  `dedicated-graalvm-isolate`, `sandboxed-process`, `wasmtime`.
  Unknown and null values reject distinctly and never fall back to a
  permissive default.
- `ActorRuntimeOptions.GcMode` and `TenantIsolationMode` default to
  `hybrid` and `trusted-inprocess`; `ActorRuntimeOptions.Validate()`
  enforces both **before** allocation of the actor arena budget or
  construction of dispatcher domains.
- An eight-flag `OwnershipProof` is plain caller-controlled data.
  Even an all-`true` record with `Backing.ExplicitNativeRegion` or
  `Backing.LlvmNativeArena` is **not an unforgeable compiler
  witness**. Every public request for strict zero-guest-tracing rejects.
  CLR NativeAOT is not equivalent to zero whole-process managed GC.
- `ActorGuestHeap32` retains actor-bound checked Ref32 handles,
  generation exhaustion without wrap, and host-runtime I64 field
  integrity. The typed host regression
  `TypedReferenceCycleAndReadLoanDelayPhysicalReclaim` constructs a
  two-node cycle and verifies logical STOP, read-loan-delayed physical
  refund, and eventual native slab reclaim despite non-owning links.

## CI/test evidence gates

`verification/Ores.Net.Tests/ActorHeapAdmissionParitySelfTest.cs`
tests valid hybrid and ownership-first host startup, null/unknown
diagnostics, invalid GC and tenant modes at both policy and runtime
construction, and the all-true forged-proof negative case.

`verification/Ores.Net.ActorMemoryProbe/Program.cs` additionally
requires policy rejection **inside an actual Linux/Windows NativeAOT
binary**, alongside the actor heap probe. The
`actor-memory-native-aot.yml` workflow requires three dedicated
pass receipts:

- `PASS actor-heap-java-parity` (managed actor test runner)
- `PASS actor-ref32-cycle-reclaim` (managed actor test runner)
- `PASS actor-heap-java-parity-nativeaot` (executed native host probe)

These receipts count **only after** exact-head stepful CI executes with
working SDK/compiler and a native binary actually runs. A zero-step
GitHub Actions failure is a runner-admission failure, not test evidence.

## Still not equivalent

Java `ActorOwnedGraphArena` owns host `AutoCloseable` payloads with
explicit per-object close attempts; the .NET `ActorGuestHeap32` deals
with actor-owned **native bytes** and drops native allocations. Physical
slab retirement zeros/frees native payload, but does not automatically
invoke arbitrary guest class destructors. Compiled AttoLang `defer`,
`raise`, `panic`, `throw` and all-exit destructor/borrow proofs
require independent source-compiler and NativeAOT execution.

Likewise, Java's `ActorPhysicalTurnGate` implements a bounded
cross-domain physical-turn FIFO admission mechanism and
`ActorTurnWatchdog` observes long-running turns. The current .NET
actor dispatcher has per-domain capacity and per-actor turn
serialization, but is **not certified equivalent** to those two
Java facilities; their queuing, permit release, close and safepoint
semantics require a separate shared contract and executed tests.

Strict tenant isolation is still **not implemented** by either ordinary
in-process actor ownership API. A .NET `ActorKind.Untrusted` worker
pool is not an OS sandbox. Do not relax the gate until actual
isolate/process boundaries and authority checks exist.

## Next completion

1. On an **authorized runner**, execute .NET 10 hosted tests plus
   Linux and Windows NativeAOT host probes at the exact Git head,
   collecting toolchain, job steps and per-binary SHA-256 receipts.
2. Extend .NET's narrowed `.atto` `Node` compiler PR #449 into
   general nominal allocation, deterministic drops and borrow-checked
   suspension; execute positive and failing native source fixtures.
3. Reuse the shared five-backend conformance PR #698. Do not claim
   Java/.NET object graph parity based on host-only memory arenas.
4. Separately design/test bounded physical-turn FIFO admission and
   cooperative watchdog parity with Java; never infer it from carrier
   thread counts.

Security: this repo's private source has **no active approved
alternate-org transfer snapshot**. Source-free checks may run
elsewhere, but private Git-tree mirroring requires independent,
source-repo-approved destination/reviewer/exact SHA+tree first.
