# .NET Core Type Registry

**Status: source implementation candidate; exact-head .NET build, CIL and NativeAOT test evidence required.**

## One canonical source of truth

`Ores.Net.Compiler.Core.CoreTypeRegistry` records the .NET backend's source-level canonical identities and safety obligations. It supplies type identity, profile, generic arity, ownership category, allocation requirement, drop policy, actor-transfer/Send policy, and backend lowering status.

This registry is not a process-global runtime object, allocator, type eraser, or promise that every listed type is executable. A registry hit establishes only the declared identity and requirements. The structured parser must call `RequireExecutableStructuredNet` before admitting any operation to its typed IR/CFG.

## Current .NET capability boundary

The structured .NET/NativeAOT frontend has a deliberately narrow executable subset:

- `int` and `bool` use the existing scalar CIL/NativeAOT ABI.
- `Option<int>`, `Some(int)`, and `None` are the existing closed tagged-scalar subset.
- `Array<int>` is the existing inline fixed-array scalar subset. It is immutable and val-only; mutable borrow, resize and general aggregate ownership are not admitted.
- `Result<T,E>`, `Ok`, and `Err` are represented in registry metadata but remain `ContractOnly`: tagged-sum CIL, Error payload ownership and NativeAOT guest-control lowering are not implemented.
- Owned `alloc::String`, `List`, `Vec`, `Map`, `Set`, and `Box`, plus explicit-only `HashMap`, `HashSet`, and `Entry`, remain unavailable to this structured profile.

This is intentional fail-closed behavior, not a parity claim. The .NET backend's legacy/lowercase string view is kept separate from an owned `alloc/string/String`.

## Borrow-checking boundary

The backend has no general typed affine/move/borrow/share HIR for arbitrary generic sums and containers. Its structured source preflight rejects `rt copy`, `rt take`, `rt borrow`, `rt share`, `rt mut borrow`, mutable sharing, and other operations which would need a verified owner/loan plan. It must not silently turn those operations into CLR object references or host-managed copies.

The only currently admitted `Option<int>` payload is an integer Copy scalar. `Option<Box>`, nested Option, and other affine payloads are rejected; no claim is made that the .NET backend can inspect a borrowed generic Option/Result or move its active payload. Result stays contract-only until ownership-aware tagged-sum lowering exists.

Registry classifications:
- `COPY`: current compiler scalar semantics prove copy and Send.
- `PAYLOAD_DEPENDENT`: a future complete implementation must recursively derive Copy/Send from every possible payload.
- `BORROWED_VIEW`: cannot become owned or Send merely by name.
- `OWNED`: deterministic backing-storage destruction is required.
- `TRAIT`: a compile-time contract, not a runtime value.

Drop policies distinguish active sum payload, all initialized aggregate members, and owned storage. `Option`/`Result` use `ACTIVE_PAYLOAD_ONCE`; arrays/tuples/pairs/entries use `ALL_INITIALIZED_MEMBERS`; owned containers use `OWNED_STORAGE`.

## Parser and test integration

The structured parser consults the registry for supported `int`, `bool`, `Option<int>`, and `Array<int>` lowering. Unsupported core/alloc entries fail with `E_CORE_TYPE_LOWERING_UNSUPPORTED` at a source offset. Wrong generic arities fail with `E_CORE_TYPE_ARITY`. User declarations cannot replace compiler-owned registry names (`E_PRELUDE_SPOOF`).

`verification/Ores.Net.Tests/CoreTypeRegistryTests.cs` checks metadata invariants, canonical identities, Option/Result arity and drop policy, view/allocation/Send classifications, profile visibility, existing structured subset acceptance, unsupported Result/alloc/affine rejection, and spoofing. It is invoked by the existing verification executable alongside real Option<int>/Array<int> CFG and emitted-CIL tests.

This document and its registry tests are not a passing build receipt. Actual .NET 10 build, NativeAOT publish, emitted CIL and runtime guest tests must execute against the exact PR head before the PR can be promoted.
