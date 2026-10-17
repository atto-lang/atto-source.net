# .NET isolation model

Oreslang's .NET backend distinguishes **code-loading isolation** from a
**security boundary**.

Modern .NET has one AppDomain. `AssemblyLoadContext` can isolate assembly
resolution/versioning and support unloadable managed code, but it does not
restrict the permissions of code in the process. Therefore Oreslang must never
use an ALC as the sandbox for semi-trusted or untrusted actors/functions.

## Production profiles

| Profile | Trust | Artifact | Security boundary | Strict NativeAOT |
| --- | --- | --- | --- | --- |
| same process | trusted | NativeAOT | no | yes |
| collectible ALC | trusted development only | managed CIL | no | no |
| worker process | trusted/semi-trusted | NativeAOT | yes | yes |
| worker process + OS sandbox | untrusted | NativeAOT | yes | yes |
| WASM sandbox | untrusted/portable | wasm32-wasi | yes | host-dependent |

The current release architecture remains strict NativeAOT. That makes the
normal production isolation boundary a **process generation**:

```text
CONTROL / supervisor
        |
        +--> trusted in-process actor/runtime
        |
        +--> NativeAOT worker process
        |       + bounded IPC
        |       + capability manifest
        |       + resource quotas
        |       + OS sandbox for untrusted code
        |
        +--> WASM worker
                + linear memory boundary
                + capability-limited WASI/import surface
```

## Hot code loading

The .NET backend already forbids runtime dynamic assembly loading in release
artifacts. Hot replacement therefore uses process generations rather than
`Assembly.Load*`:

1. compile the new Oreslang generation;
2. publish a new NativeAOT worker;
3. launch with an explicit capability/resource policy;
4. validate health and contract compatibility;
5. switch the proxy/router;
6. drain and terminate the old generation.

This maps well to Grandaddy/Daddy/Child semantics: CONTROL owns generation
routing; a child generation cannot widen its own authority.

## WASM

WASM is an excellent hostile-code boundary, but it is a different artifact
target. The .NET backend must not pretend that arbitrary generated CIL becomes
safe merely because a .NET host embeds a WASM engine.

Use either:

- the Oreslang WASM backend; or
- an LLVM -> wasm32-wasi lowering path.

A .NET host may supervise those WASM workers, but the sandboxed payload is the
WASM artifact, not the CIL assembly.

## NativeAOT worker candidate

The backend now contains a concrete candidate worker protocol and statically
linked worker entrypoint:

```text
oreslang-app --worker
      |
      +-- ORW1 / v1 binary frame header
      +-- strict UTF-8 payloads, <= 1 MiB
      +-- nonzero request IDs
      +-- absolute invocation deadlines, <= 30 s
      +-- ready / invoke / cancel / response / error / shutdown
```

The original scalar host still exposes `main() -> i64` and deliberately
requires an empty request payload. A separate Lambda source/compiler candidate
now emits `Oreslang.Generated.OresProgram.Invoke(String) -> String` and is
statically linked by `Ores.Net.LambdaNativeHost`. Its ORW1 runtime takes the
normalized raw UTF-8 request payload and produces a strict UTF-8 response.

This is source-connected but **not runtime-certified**: exact-head .NET Actions
still fail before allocating a runner, so neither the generated CIL nor the
NativeAOT Lambda worker has an execution receipt yet.

Deadline admission happens before guest entry. The scalar bootstrap is not yet
cooperatively cancellable once guest code is running, so `cancel` returns a
specific unsupported/cooperation error and the supervising host remains
responsible for deadline-triggered **process-tree termination**. Unexpected
guest exceptions produce one opaque `guest-failure` response and retire the
worker instead of reusing possibly corrupted state.

The generic .NET process-start factory clears the ambient environment, uses an
absolute executable path, redirects all stdio, rejects shell execution, and
admits only deny-all capabilities. It **refuses untrusted workers**: those must
be launched by a race-free OS-sandbox adapter.

The machine contract is `contracts/v1/dotnet-worker-v1.json`.

## Scintilla integration boundary

Scintilla draft PR `gleam-lambda-runner#56` now contains a dedicated
ready-gated ORW1 port driver for canonical runtime `oreslang-dotnet`. The
adapter is argv-only through the existing isolation launcher and rejects
shell/provider/container and NATS pool fallback.

For this runtime only, Scintilla passes the normalized request payload directly
through ORW1. It does not inject the ordinary
`{slug, definition, request}` child-runtime envelope; definition and routing
metadata remain host authority.

The Scintilla source repository is private and its source-org Actions currently
also fail before runner allocation. A cross-org test carrier was attempted but
failed before source checkout and was closed; no private source was transferred.
Therefore the adapter remains source-only evidence.

## ORW1 supervisor enforcement

The worker reserves stdout exclusively for ORW1 frames. Guest stdout/stderr are
redirected during entry execution to a bounded log sink. Host-provided wall/output
limits may tighten but never widen protocol ceilings. The generic
`WorkerProcessClient` requires an exact ready handshake, serializes invocation,
uses monotonic request IDs, bounds/discards stderr, and retires/kills the process
tree on deadline expiry, caller cancellation, or protocol failure.

These controls are lifecycle enforcement, not the untrusted OS sandbox. Generic
launch continues to reject untrusted manifests.

## Next implementation steps

- obtain exact-head .NET 10 + NativeAOT execution for the new Lambda compiler,
  host, and `verification/assert-lambda-worker.py`;
- obtain source-org execution for Scintilla ORW1 adapter PR #56;
- bind `oreslang-dotnet` to an explicitly reviewed
  `ores-proc-isolation-cli` policy and produce an OS-isolation receipt;
- add Windows Job Object and Linux namespace/cgroup/seccomp qualification where
  not already supplied by the isolation CLI;
- expand the narrow Lambda parser from literal equality guards into shared typed
  OresIR without weakening fail-closed admission;
- add cooperative cancellation once `await` / suspension lowering exists;
- later add a WASM-host adapter without weakening the CIL policy.
