# .NET NativeAOT tracing parity — logical continuation substrate v1

## Scope

This is a **trusted-host runtime foundation**, not a claim that the .NET
compiler can yet lower `using telemetry.span("checkout") as span { ... }`
into CIL or that this code runs AttoLang guest `suspends` functions.

The source exactness and acceptance goals are shared with Java/GraalVM
[PR #3037](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/3037)
and LLVM [PR #1716](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/pull/1716).

New production library `NativeTraceContinuationV1` provides:

- Canonical W3C traceparent v00 parsing/formatting; rejects wrong version,
  invalid or uppercase hex and zero trace/span IDs.
- Host-minted, reference-identity-bound owner tickets with nonzero actor/task
  generation and independent owners per tracer instance.
- Explicit `Suspend` / `Resume` across physical CLR threads **without**
  `AsyncLocal`, `ExecutionContext` delegation, dynamic IL or guest callbacks.
- Exactly-once terminal success/error/cancellation via atomic state transition.
- Supervisor `Retire(owner)` closes remaining logical scopes, fences stale
  generations and prevents late task resumption.
- Explicit caps for active scopes and immutable completion receipts; nonblocking
  CAS reservation and drop accounting. No exporter, network I/O or clocks.
- Off-by-default path: no random-ID mint or scope allocation.

`OresParser` and `OresStructuredParser` now lexically classify the canonical
`using telemetry.span` syntax as `E_TELEMETRY_SCOPE_UNSUPPORTED` instead of
accidentally treating it as an ordinary call. Source comments cannot trigger
the gate. This refusal is **intentional** until CIL scope lowering has
verified cleanup across every return/trap and no-escape analysis.

## Executable regression

`NativeTraceContinuationV1SelfTest.Run()` is called by the existing
`verification/Ores.Net.Tests` executable. Cases: strict W3C input,
parent-child identity, disabled path, owner and generation nonforgeability,
carrier migration, duplicate suspend/resume rejection, active/receipt
capacity, error completion, supervisor STOP cancellation, exactly-once
retirement, and 128 concurrent finish/retire races.

### Required proof before promotion

1. A .NET SDK 10 runner executes `dotnet run --project
   verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release`
   with nonzero step records. This branch cannot claim passing managed tests
   until that has happened.
2. Publish and execute an actual NativeAOT tracing host test on Linux and
   Windows, confirming no CLR reflection/JIT dependency.
3. Add a checked `StructuredUsingTelemetrySpan` AST, typed scoped span
   nonescape, `try/finally` CIL emission on normal/early/exception exits,
   and a real guest fixture; **only then** remove the parser refusal.
4. Implement task/supervisor-owned source `suspends` frame lowering and
   bind compiler-minted scopes to authenticated scheduler owner/generation
   evidence. Host-side `MintOwner` by itself does not authenticate actors.
5. Run the same actual `.atto` source and terminal-span oracle on Java,
   LLVM and .NET with full source-tree identity and independent review.

Do not expose `Owner`, `Scope`, or the owner mint operation to guest code.
No OTLP, JIT, entire-backend parity or actor security sandbox is claimed.
