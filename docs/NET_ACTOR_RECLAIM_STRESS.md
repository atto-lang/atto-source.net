# Canonical .NET ActorArena tail reclamation and lifetime stress

Status: **draft integration regression suite**, not release certification.

This branch merges native tail-coalescing draft #130 into the canonical
integrated actor runtime draft #141. Git parentage retains both original
review branches; #125/#133/#134 are not rewritten or merged.

## Host invariants under test

- The actor retains one stable root while smaller persistent objects are
  allocated, freed out-of-order, reused, and observed through exact-issued
  handles. Four fixed xorshift32 seeds each exercise 1,024 operations, with
  narrow admission-failure handling and per-operation live-byte accounting.
- Every newly admitted native allocation starts zeroed; no stale handle
  regains authority when a slot generation changes. Existing live values
  remain unchanged after unrelated frees or allocator tail coalescing.
- Removing all temporary objects permits an aligned 480-byte allocation
  alongside an 8-byte permanent root in a 512-byte persistent region.
- A lifetime-pinned actor may be logically retired while still physically
  resident. A retired/stale physical turn cannot resume or dereference.
  Off-carrier cancellation cleanup releases its lifetime token twice safely,
  and the **final real release** refunds the process budget exactly once.
- Existing #141 tests additionally check cross-runtime global capacity
  accounting and 24 rounds of concurrent off-carrier pin disposal.

## Reproduction

Run at the **exact reviewed commit SHA** with .NET 10 from the originating
private repository on a trusted host:

```sh
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
```

Required before review approval: a real Windows NativeAOT publish/execute
check, Linux diagnostic run, and explicit GitHub Actions job steps at the same
commit as independently verified by draft #139. Source-org Actions still
have a pre-job scheduling failure; zero-step runs are **not** C# failures or
test passes.

This is **host-level allocator and lifetime behavior**, not source compiler
ownership lowering, actual async cancellation/FFI callback scheduling,
hard-real-time certification or proof of eliminating CLR GC. These require
separate compiled source-language tests, randomized scheduler interleavings
with captured seeds, NativeAOT execution, fuzzing and formal verification.
