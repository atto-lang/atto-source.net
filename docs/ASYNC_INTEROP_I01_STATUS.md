# Async interop I-01 candidate

Related: issue #183; shared conformance draft #192. All 62 guest vectors remain pending.

## Implemented scope

`HostTaskObservationScope.Observe` observes one already-started System Task
through a distinct TaskCompletionSource projection. A caller-provided observer
token cancels only WaitAsync, never the source operation. Source cancellation
remains cancelled without copying its token into the projection; a faulted
OperationCanceledException remains faulted. Multiple failure causes survive.
Per-scope pending observation capacity is reserved before registration and
returned when forwarding exits. No Task.Run, reflection or new executor is used
by the helper. TaskCompletionSource forbids inline user continuations.

This is trusted-host code, NOT an Oreslang Future/Task/Observable implementation.
System Task continuations are not an admitted Oreslang scheduler or actor lane.
No guest callbacks, affine sharing admission, panic conversion, producer ownership
transfer, generation fences or real-time profile support are provided. The API
must not be bound to guest syntax until those proofs exist. Zero pending
observations does not certify producer/actor physical quiescence.

## Fail-closed compiler boundary

Both scalar and structured parser regressions demand the specific unsupported
Task-boundary diagnostic through whitespace, nested comments and all supported
line separators. The shared Task lookahead previously ended line comments only
at LF; it now agrees with the lexers on CR/NEL/LS/PS as well. This fixes diagnostic
evasion, not a previously successful Task execution path.

Other unsupported conversion forms are checked for containment only. Those broad
parser rejections do NOT satisfy I-04 intended type/cardinality diagnostics.

## Execution gates

Local SDK unavailable when authored: C# compilation and all execution PENDING.
The workflow runs the full managed compiler/runtime suite and publishes/runs a
separate host-only NativeAOT probe for Linux x64 and Windows x64. Successful
execution of that probe establishes only this host I-01 subset, not guest parity.

The hardening follow-up also runs a real NativeAOT compiler: a scalar positive
control must emit CIL, then eight unsupported inline conversions must exit 1
with the intended Task-boundary diagnostic and leave no output assembly.
Host tests include sibling observer isolation, sticky success under late
cancellation, and 32 simultaneous admission attempts against four permits.
These still do not establish positive source interop or actor quiescence.

Commands from the repository root:

    dotnet run --project verification/Ores.Net.Tests -c Release
    dotnet publish verification/Ores.Net.InteropProbe -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o artifacts/interop-probe
    ./artifacts/interop-probe/Ores.Net.InteropProbe

Require exact source commit/tree, nonzero job steps, probe binary SHA-256 and
complete logs before recording host execution. A zero-step run is NOT_EXECUTED.
I-01 overall and I-02 through I-08 remain pending real compiler/runtime, scheduler,
ownership, negative-diagnostic, trace and physical-retirement receipts.
