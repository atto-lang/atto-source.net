# Current-main .NET serial actors and memory integration
Draft; host runtime only. Guest .ores actor/compiler lowering, independent CLR
heaps and protected untrusted execution are not implemented by this integration.

## Source integration

Main: 30e7742503f857372bd96f12e858bd71c4a2fdb6.
Candidate #111: 3d014cb26ba115d86232c7f95342d7be26e6d9a4.
Common ancestor: 9607bd2e04cb59943351f87847a110303f85f1a6.
Candidate is 258 commits behind main; replacing the repository with it would
discard current compiler work. Only runtime and verification files were merged
with git merge-file against that ancestor. Nine files merge/add cleanly; the
Program.cs conflict is resolved additively, preserving every current-main line
in order and adding ActorDispatchDomainsSelfTest.Run and ActorRuntimeSelfTest.Run.
No compiler, host or shared build-property files are replaced.

contracts/v1/actor_memory_integration_provenance_20261027.json records the twelve
runtime/test/probe input result blobs and main/ancestor/candidate blobs where
present. This is source provenance, NOT executed runtime evidence.

## Implemented host substrate

* Serial actor cells own one bounded authenticated mailbox and unmanaged arena.
  Spawn is lazy and creates no carrier; three bounded runtime-wide pools dispatch
  PRIVATE/SHARED/UNTRUSTED kinds. "Untrusted" is a resource/scheduling bulkhead,
  not a sandbox against arbitrary CLR/unsafe code.
* Exact issued persistent/scratch handles, loans, pins and active turn identities;
  physical turn tied to its admitting carrier; no native pointer exposed.
* Per-actor/runtime plus process native budgets, exception-neutral allocation,
  tail temporary reuse and generation fencing.
* Mailbox admission preflight before starting a carrier, schedule-before-publish,
  per-turn delivered-buffer scrubbing and physical shutdown joining.
* Lifetime pins delay native release after logical actor removal. LiveActors==0
  alone is NOT an arena-reclamation receipt; NativeReservedBytes may remain >0.

## Verification gates

Existing full verification runner retains all main tests plus the actor arena,
dispatch, shard and runtime regressions from the candidate.
New reflection-free Ores.Net.ActorMemoryProbe publishes NativeAOT and executes
real three-kind actor cells. Its assertions cover independent persistent values,
foreign read/write-handle denial, zero-carrier spawn, bounded shared pools,
expired context denial, STOP held by a pin and off-carrier final refund.

Local merge checks PASS: no conflict markers; Program.cs equals main exactly
after removing the two added runtime suite invocations.
Runtime/.NET10/NativeAOT tests are NOT_EXECUTED locally: no dotnet SDK.
The exact-head source-local workflow requires the existing verification runner
and actual native probe execution on Windows x64 and Linux x64. Publication
alone is not a pass. Keep draft pending executed non-skipped tests and review.

Forge #117 is now the matching current-main host arena integration; Java/Graal
#2157, LLVM #1086 and WASM #153 remain companion unmerged fixes. Full five-backend
source actor isolation parity remains pending.
