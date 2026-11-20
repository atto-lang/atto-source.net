# Oreslang OTel guest parity: strict .NET Native AOT toolchain

This backend participates in the [shared five-backend/six-profile OTel candidate contract](https://github.com/ores-truffle-oreslang/oreslang-platform-conformance/pull/695).
The checked-in `verification/otel/backend-admission-v1.json` lists the **actual current blockers** and the source-owned `oreslang-otel` fixture/implementation Git blobs. It is intentionally **NOT_CERTIFIED**, not a capability claim.

## Current source reality

The independent `oresnet` compiler currently emits a checked signed-i64 CIL scalar subset. It does **not** compile the imported `Span` class, `String`/JSONL stdlib telemetry or actor-local event hooks in the unchanged source-owned OTel fixture. Managed .NET host calls or Roslyn-generated C# are not evidence of native Oreslang guest support.

## Required compiler-backed integration

Extend the source/CIL frontend with imports, checked string and class semantics, source stdio and clocks while preserving ownership. Compile the unchanged smoke fixture and publish the **compiler and application as .NET full Native AOT** on Windows/Linux; execute the real produced guest with bounded stdout capture and no JIT/dynamic CIL fallback. Run tenant/actor-fenced telemetry and slow-sink shutdown cases before claiming parity.

## Fail-closed source-owned admission

```sh
node verification/otel/verify-backend-admission.mjs --self-test
node verification/otel/verify-backend-admission.mjs --require-executed-guest
```

The **first** command tests the source-only capability manifest and fourteen hostile promotions; a green result reports `SOURCE_ONLY_CANDIDATE`, **never guest parity**. The **second** command must fail until a separate reviewed real compiler/guest integration and independent artifact corroboration exists. The source-local `otel-guest-admission.yml` checks both behaviors with pinned checkout, exact original PR-head assertion and no external private source checkout.

A real candidate must preserve the original `oreslang-otel/tests/smoke.ores` file and `src/otel.ores` import source at the approved Git blobs, execute the correct **backend compiler and runtime**, and provide raw stdout for `bin/verify-otel-guest-parity.mjs` in the separate conformance checkout. The checker requires six log records, two correlated completed spans, one metric, and recorded compiler/artifact/stdout digests. Even a passing checksum and schema is **self-reported evidence** until the independent trusted job auditor confirms build/run provenance and nonzero executed steps.

## Delivery and release gates

- A status check that ran zero steps is **NOT_EXECUTED**; host-only manifests, fixture emitters, test sinks and static documents are never promoted to compiler guest evidence.
- Telemetry must be actor-local and explicit, with immutable IDs across actors, bounded nonparking output, positive or explicitly reported backpressure, proper finalization and credential/PII redaction.
- `ores_trace_id` is opaque and `oreslang-otel.v1` JSONL is **not OTLP**. A separate explicit mapping to standard OTLP trace/span IDs and W3C propagation is required before exporting to the ORES OTEL collector.
- Collector staging [PR #13](https://github.com/ores-otel/ores-otel-monorepo/pull/13) is a non-production receiver. Independent real exporter/storage query readback, workload auth and multi-tenant isolation are separate release gates.
- Keep this PR **draft** until real guest execution and independent review; do not copy private source to an unapproved public/private carrier, and do not add Python scripts or dependencies.
