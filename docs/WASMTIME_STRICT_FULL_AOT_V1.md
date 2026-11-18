# Strict AOT Wasmtime x .NET Native AOT candidate

**STATUS: experimental, NOT_EXECUTED, and NOT production tenant admission.**
See PR #563. The shipping `OresVmHost.WasmtimeSandbox` remains fail-closed.

## Two different AOT obligations

1. The .NET trusted host must be published via `dotnet publish
   -c Release -r linux-x64 -p:PublishAot=true`. CIL must be fully
   compiled to machine code before deployment; no assembly load or CIL JIT.
2. The *untrusted guest* must be compiled from validated Wasm by the
   trusted build control-plane **before** it enters the host. The runtime
   executes only authenticated precompiled Wasmtime artifacts. A normal
   `Module.FromBytes` call compiles Wasm in the runtime and is forbidden
   on this strict release path.

```text
  Trusted build/release infrastructure                Deployed data plane
  ┌─────────────────────────────────────┐           ┌───────────────────────┐
  │ Atto source -> verified Wasm        │           │ .NET CIL -> Native AOT│
  │            -> Wasmtime precompiler  │           │ trusted OresVmHost     │
  │            -> .cwasm native artifact│           │    (future integration)│
  │            -> signed manifest       │           │     |                 │
  └───────────────┬─────────────────────┘           │ authenticated .cwasm │
                  └── reviewed release pipeline ───>│ Module.Deserialize()  │
                                                    │ independent Stores    │
                                                    └───────────────────────┘
```

## Implemented synthetic CI proof (NOT Atto source lowering)

Two opt-in projects with `PublishAot=true`, a pinned Wasmtime 50.0.1
NuGet dependency and `build/StrictNativeAot.targets`:

* `verification/Ores.Net.WasmtimeAotBuilder`: trusted **build-time**
  Native AOT executable that compiles a hard-coded WAT fixture into a
  Wasmtime native artifact, computes its SHA-256 identity and creates a
  versioned manifest signed by an **ephemeral test RSA-3072 key**.
  Only the test public key is written to disk. The private key is never
  exported. This test WAT does **not** originate from the Atto compiler.
* `verification/Ores.Net.WasmtimeAotRunner`: separate **execution-time**
  Native AOT executable. It has no reachable Wasm compiler functions in
  source. It verifies the signed manifest with RSA-PSS/SHA256, checks
  exact version, RID, engine feature profile, ABI, source hash syntax,
  native artifact size and SHA-256. It refuses signature changes,
  swapped artifact bytes and a different issuer. Only **after** verifying
  the in-memory artifact does it call `Module.Deserialize`, which avoids
  recompiling the Wasm guest. The probe uses an empty linker, separate
  private Stores, fuel limits and neighbor-survival checks.

```sh
# On a trusted Linux x64 SDK/native compiler runner:
dotnet publish verification/Ores.Net.WasmtimeAotBuilder/Ores.Net.WasmtimeAotBuilder.csproj \
  -c Release -r linux-x64 -o /tmp/atto-aot-builder -p:PublishAot=true
dotnet publish verification/Ores.Net.WasmtimeAotRunner/Ores.Net.WasmtimeAotRunner.csproj \
  -c Release -r linux-x64 -o /tmp/atto-aot-runner -p:PublishAot=true

/tmp/atto-aot-builder/oresnet-wasmtime-aot-builder /tmp/atto-synthetic-artifact
/tmp/atto-aot-runner/oresnet-wasmtime-aot-runner \
  /tmp/atto-synthetic-artifact/guest.manifest.json \
  /tmp/atto-synthetic-artifact/guest.manifest.sig \
  /tmp/atto-synthetic-artifact/guest.cwasm \
  /tmp/atto-synthetic-artifact/ephemeral-ci-public.pem
```

The dedicated workflow `.github/workflows/wasmtime-strict-aot.yml`
asserts the exact commit, compiles **both** executables with Native AOT,
runs the builder first and the native runner second, and requires explicit
PASS markers from both. A workflow that fails with zero executed steps
is **NOT_EXECUTED**, not a test result. This task has no local .NET SDK.

## November 2026 Wasmtime advisory gate

**Release HOLD:** the synthetic proof is currently pinned to Wasmtime
50.0.1. Upstream released **50.0.2 on 2026-11-16** with fixes for
[GHSA-43j2-gcp4-m22g](https://github.com/bytecodealliance/wasmtime/security/advisories/GHSA-43j2-gcp4-m22g)
(stale Wasm GC heap-base abort) and
[GHSA-2674-w29h-wqvj](https://github.com/bytecodealliance/wasmtime/security/advisories/GHSA-2674-w29h-wqvj)
(runtime built without threads feature can abort). The current narrow
fixture disables reference types and threads and imports no host APIs,
but this does NOT certify new actor workloads against these advisories.

Do not authorize production tenants while running the unpatched 50.0.1
native library. Upgrade both the **verified .NET package** and the actual
RID-specific Wasmtime native library together when the patched binding
is available; recompile signed guest native artifacts under the matching
engine build/configuration. The Rust upstream release by itself is not
proof the matching NuGet package or compiler-free C-API build exists.
Review the native library provenance and execute exact-head tests first.

## Explicit production security blocks

**1. Trust anchor:** The synthetic builder emits its own ephemeral test
public key; the test runner accepts that key by argument. Therefore
neither the test signature nor its digest proves authorization to execute
an artifact from a real tenant. Production must embed a vetted, stable
release public key (or equivalent independently authenticated key
distribution), with protected signing credentials, signed review-approved
source provenance, key rotation and revocation. Never accept a tenant's
manifest and public key together as authority. Keep the compiled
native artifact immutably bound to signing identity and build metadata.

**2. No compiler in deployed process:** The current Wasmtime NuGet native
library is not certified compiler-free. The test runner avoids guest
compilation API calls, but a stricter runtime guarantee requires a
reviewed matching Wasmtime **C API library built without Cranelift/Winch
compiler features**, containing only the support needed to deserialize
trusted precompiled modules. The C ABI and Native AOT compatibility need
real published-executable proof. Wasmtime native precompiled artifacts
are unsafe to deserialize if they are attacker-controlled. Never use
`Module.DeserializeFile` on an untrusted or raceable path; verify the
actual bytes and deserialize those bytes in memory.

**3. Artifact portability:** A signed artifact must bind Wasmtime
version, target RID/CPU features, engine configuration, binary identity,
source identity, ABI, imports and feature grants. Version 50.0.1
may require architecture-specific artifacts. Cross-compile only with an
approved pinned toolchain; don't assume the same artifact is portable
between Linux x64, Linux ARM64 and Windows.

**4. Guest policy:** A successful synthetic fixture is not proof of a
real Atto compiler-generated actor. Add explicit compiled Atto source
parity, bounded checked message ABI, quotas for total linear memory,
tables, stack, native code, host resources, scheduler fairness and
shutdown/cancellation. Retain generation-fenced handles. Do not enable
CIL, native FFI, WASI or unrestricted CLR object access for tenants.
One process still has a shared host-bug and crash failure domain.

**5. Windows CET:** Windows untrusted admission remains blocked due to
hardware-signaled trap / CET fail-fast risk. Do not disable CET.

## Source references

* https://docs.wasmtime.dev/examples-pre-compiling-wasm.html
* https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.Module.html
* https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
