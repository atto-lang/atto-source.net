# .NET Atto std telemetry source candidate

The native `stdlib/telemetry.atto` module is implemented **separately** from Java/Graal and LLVM. It provides pure span-field/sampling/budget policies and a non-authoritative immutable `SpanSnapshot` value. This is proposed .atto source, not demonstrated NativeAOT guest support.

A .NET runtime bridge may eventually export to the OpenTelemetry .NET SDK and OTLP, but must preserve Atto's trusted context/actor envelopes, tenant boundaries and explicit network permissions. It must not treat guest-created `SpanSnapshot` as an authenticated span and must not rely only on thread-local state to bridge `await` or migrating actors.

The language-level API remains compiler-owned:

```atto
use std.telemetry as telemetry;

pub fnc work(): void {
  using telemetry.span("work") as span {
    span.add_event("begin");
  }
  return;
}
```

This **target syntax is not proven to run in .NET**. The stdlib cannot implement its own guest `span()` or `close_scope()` pretending to own privileged tokens. Instrumentation modes `none`, `boundaries`, `calls` and `full` define a compile-time ceiling; trusted controls may activate only compiled probe sites. Strict sealed NativeAOT forbids live JIT injection. A separately signed/precompiled hotloaded version is a new code generation, not patching the current binary.

## Required tests before readiness

- Parse, import/link and actually execute .atto helpers in the .NET compiler, including invalid version/zero IDs/overflow and `RECORD_ONLY` vs sampled.
- Compiler-owned scope-alias forgery/escape/manual-close denials; normal return, failure, `await`, cancellation and actor generation replacement with exactly-once completion.
- .NET NativeAOT real binary, dormant probe OFF→ON→OFF without restart, strict `none` denial and allocations/latency measurements.
- W3C traceparent/tracestate wire parser and actor/async propagation, authorized bounded OTLP export with a real receiver and permission-denied cases.
- Exact current Git tree, material nonzero-step CI, independent review. None of these compiler-backed tests are claimed in this source-only PR.

Coordinate with [Atto spec #167](https://github.com/atto-lang/atto-spec/pull/167), [common instrumentation #122](https://github.com/atto-lang/atto-common-source/pull/122), Java using PR [#1661](https://github.com/atto-lang/atto-source.java/pull/1661) and LLVM runtime trace [#785](https://github.com/atto-lang/atto-source.llvm/pull/785). API expectations align at the boundary, but .NET source is independently owned.
