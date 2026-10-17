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

## Next implementation steps

- add a NativeAOT worker protocol with bounded MessagePack/string envelopes;
- bind worker launch to explicit Deno-style Oreslang capabilities;
- add Windows Job Object and Linux process/container resource adapters;
- add cancellation/deadline propagation and process-tree teardown;
- produce exact-head NativeAOT execution receipts;
- later add a WASM-host adapter without weakening the CIL policy.
