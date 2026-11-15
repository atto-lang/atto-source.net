# .NET Native AOT near-zero-GC tracing audit — 2026-11-13

Status: source hardening candidate, **NOT EXECUTED** at this SHA. This change is layered on draft tracing PR #512. It is not a guest .atto execution proof.

## Measured boundary and fixed hotspot

`NativeTraceParentV1.TryParse` previously built a temporary `"00-"` substring for every 55-character candidate, and constructed trace/span substrings **before** rejecting all-zero identifiers. Untrusted malformed trace headers could therefore cause avoidable CLR allocation. Validation now operates on `ReadOnlySpan<char>` and allocates the two immutable ID strings **only after** accepting the header.

`NativeTraceContinuationV1.Begin` previously formatted a constructed `NativeTraceParentV1` to a W3C string and reparsed it for every parented scope. Direct `IsValid()` checks preserve strict lowercase hexadecimal length and nonzero ID requirements without this redundant allocation.

The self-test warms all negative parser branches, then checks 70,000 rejected headers using `GC.GetAllocatedBytesForCurrentThread()` on **one physical carrier thread**. It also rejects forged constructed parents before scope reservation. This does **not** prove system-wide zero allocations or that full Native AOT guest spans exist.

## Architectural rules / release blockers

1. Keep the guest compiler at sealed CIL-to-NativeAOT: no dynamic CLR IL generation, assembly loading, or source fallback. A trusted native variant compiler is an **external optional subsystem** and needs signature verification, code page policies (W^X), ABI/ownership/effects identity, generation pinning, quiescence and rollback before enabling `UniversalHotLoadV1`. That capability API remains unsupported.
2. Keep actor payloads, mailboxes and logical stacks in budgeted native arenas with generation-fenced handles; track process and tenant native-memory peak including temporary relocations. CLR metadata, `Scope` objects, cryptographic IDs, queue receipts and exporters remain managed allocations until explicitly redesigned and measured.
3. A production trace data plane must have bounded sampling/queueing, explicit drop accounting and a nonblocking exporter outside actor turns. `ActivitySource`/OpenTelemetry propagation may be used in compatible host boundaries, but is not itself a custom JIT.
4. Promote tracing only after executable `.atto` guest CIL → Native AOT Linux + Windows evidence (exact commit/tree/fixture/artifact hashes), trace context over suspend/resume/STOP, verified compiler-emitted scope cleanup, managed allocated-bytes/operation and GC-count deltas under warm actor churn and tracing OFF/ON, and authorized native hot-variant admission/revocation tests.

## Evidence boundaries

The regression is registered in `verification/Ores.Net.Tests/NativeTraceContinuationV1SelfTest.cs` and executed by the existing managed test runner. The runner is **not** an independent proof until a real job executes. Existing actor-native-arena allocation tests demonstrate a narrowly scoped primitive numeric-handle fast path, not a whole-process GC-free runtime. Neither the tracing host subset nor a source-only fixture counts as compiled AttoLang parity.


## Executable Native AOT near-zero-GC host probe (candidate)

New standalone `verification/Ores.Net.NearZeroGcProbe` executes the actual
runtime native-arena and W3C scope implementations in managed .NET and,
when authorized runner capacity exists, as compiled Linux x64 and Windows x64
Native AOT binaries. It is **host-only**: it does not compile a guest `.atto`.

Acceptance oracles (after warmup, same physical thread):

- 50,000 numeric-handle native arena allocate/read/write/drop cycles with a
  persistent actor root: **zero CLR allocated bytes and zero CLR collections**;
  all native tenant/process reservations refunded after physical disposal.
- 50,000 disabled trace probes: **zero managed allocations**, no scopes,
  random IDs, receipts or drops.
- 70,000 malformed W3C headers: **zero managed allocations** and strict rejection.
- 512 enabled traced scopes: **nonzero managed allocations reported explicitly**,
  at most eight retained receipts, exact drop accounting and terminal completion.

The workflow checks exact source/blob SHA, managed execution, native binary
execution on two operating systems, positive proof markers and artifact SHA-256.
A source-organization zero-step failure is **NOT_EXECUTED**, not a failing C#
assertion. Evidence at this newly authored source SHA remains **NOT EXECUTED**
until a runner completes its steps. An approved private runner destination is
not currently listed in the repository's protected export policy. Do not use an
unapproved cross-organization private-source mirror.
