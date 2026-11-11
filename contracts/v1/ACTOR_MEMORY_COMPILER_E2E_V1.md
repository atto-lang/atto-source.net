# Actor-memory compiler E2E contract v1

Status: **DRAFT / NOT CERTIFIED**. This is additive to `SHARED_ACTOR_ARENAS_V1` and
`actor_arena_quiescence_matrix_v2`; it does not supersede their safety barriers.

## Ownership and cleanup semantics

1. Every mutable guest payload belongs to exactly one actor or lexical owner.
   Moving transfers authority; a borrow never transfers ownership.
2. Persistent actor state survives turn boundaries but cannot outlive actor
   retirement. Scratch and non-transferred lexical owners are reclaimed at
   the end of their scoped lifetime, including returns, errors and cancellation.
3. `rt borrow` cannot authorize mutation; `rt borrow mut` is exclusive.
   A loan spanning `await` must remain pinned to a generation-checked logical
   continuation and must block retirement until its lifetime is settled.
4. Stale, foreign, retired and forged handles fail closed; slot and actor IDs
   never wrap into previously valid authority. Reused bytes are zeroed.
5. Actors cannot free their arena while a live physical turn, outstanding read
   loan, future/callback, FFI pin or other external lease may still reference it.
   Logical retirement happens before physical memory reclamation.
6. Ref counting may be used for explicit shared-immutable resources; it is
   neither a substitute for actor ownership nor a cycle collector.
7. On LLVM, zero *tracing* GC is the baseline target; real-time's stricter
   zero hot-path malloc/fault guarantee requires additional instrumented proof.
   .NET and Java retain managed host control planes but target zero managed
   allocations for a defined, warmed actor-turn subset, not zero process GC.

## Evidence separation

The `host_checks` list describes executable *runtime-substrate* probes.
The `guest_checks` list requires **actual guest source parsing, typed IR
ownership/drop lowering, emitted native/CIL/Graal artifact, executed guest
actor messages, cancellation and suspension**, and observed allocation
measurements. Running native/managed host utilities is not sufficient.

A supported guest fixture should create an owned object in a
`define actor` handler, mutate persistent state, create/drop a scoped
temporary, suspend and resume, handle cancellation, and terminate the actor.
A truly cross-backend guest test is **NOT_EXECUTED** until compilers support
this source and all three execute it end-to-end. Do not replace missing
compiler capabilities with a host stub, mock, simulated turn or fake receipt.

No performance or GC claim may be promoted from source-free validation.
Record the exact commit + tree, source-local or specifically approved runner,
executed job steps, guest binary hash, native memory usage, and per-turn
managed allocation bytes on .NET/Java. A zero-step CI result is
`NOT_EXECUTED`, regardless of its red/green status.

## Initial host probes (not guest E2E)

- LLVM: `tests/actor_memory_e2e_v1_host.c`, compiled with the *real*
  `ActorArena.c`; simulated turns, temporary drops, scratch reset,
  pinned retirement and three POSIX abort-on-misuse negative controls.
- .NET: `ActorMemoryE2EV1SelfTest.cs` in the existing test executable;
  warmed per-thread CLR allocation measurement around 1,024 simulated turns,
  deterministic cleanup, stale and foreign handles.
- Java: `ActorMemoryE2EV1Test.java` in the existing Maven JUnit suite;
  actor-local direct-memory ownership, simulated turns, stale and foreign
  handles; JVM allocation bytes still **NOT_MEASURED** in this probe.

These tests can establish subset host semantics, but do not establish
compiler-generated object placement, exact-head AOT, or guest zero-GC.
Other backends (Forge/WASM) continue under the existing five-backend arena
contract and are outside this three-backend **compiler E2E** slice.
