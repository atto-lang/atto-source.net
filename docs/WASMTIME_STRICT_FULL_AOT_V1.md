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

## Security re-audit: bounded artifact admission and vulnerable engine hold

**2026-11-18 — production NativeAOT Wasmtime execution remains DENIED.**
The experimental .NET packages are pinned to `Wasmtime 50.0.1`. Wasmtime
published [GHSA-43j2-gcp4-m22g](https://github.com/bytecodealliance/wasmtime/security/advisories/GHSA-43j2-gcp4-m22g)
on November 16: a host-process abort bug affecting engine versions
50.0.0–50.0.1, patched in 50.0.2. The official .NET NuGet listing checked
today still identifies 50.0.1 as the latest available managed binding.
The upstream *engine* fix is not evidence that a compatible patched .NET
binding/native library is available. Never silently substitute version
50.0.2 in PackageReference until availability and the packaged native
libwasmtime build are independently verified.

The two experimental builder/runner programs now **require**
`ORES_WASMTIME_SYNTHETIC_CI_ONLY=1` before doing work. The strict-AOT
workflow executes both binaries **without** this opt-in and demands denial
(exit 78), then explicitly opts in for trusted synthetic test execution.
This flag is a guard against accidental use, *not* a production credential,
signature, sandbox boundary or approval to run arbitrary tenants.
Even a successful synthetic CI run must not enable shipping guest admission.

The runner's `TrustedAotArtifact.ReadBoundedFile` now keeps one open
file handle, checks the size, and reads at most its configured byte limit
plus one; it never performs an unbounded `File.ReadAllBytes` allocation
after a separate file-length check. It then verifies the exact in-memory
artifact bytes before calling `Module.Deserialize`. Malformed public keys,
JSON and incorrectly typed signed manifest values fail as security
denials. Regression probes exercise the length limit, empty files,
invalid PEM, and signed invalid JSON value types.

The .NET `Config.WithReferenceTypes(false)` policy and zero-import
constraint reduce the attack surface; they **do not independently
certify that all engine GC features are disabled** or that a vulnerable
engine is safe for production. The release requirement is a verified
patched native engine with a matching compiler-free runtime, strict
imports/feature policy, and genuine Native AOT execution receipts.

The repository's
[`contracts/private-ci-security.v1.json`](../contracts/private-ci-security.v1.json)
denies private-source cross-organization mirroring without specific
protected-source approval, independent security/code-owner review, and
exact source/destination Git blob/tree attestation. Available Actions
minutes, a user request, an alternate private repo, or agent credentials
do not themselves satisfy those gates. No private source is to be
exported until they are satisfied. Existing source-org Wasmtime Actions
have failed with zero executed job steps; they are not a runtime test.

## Patched compiler-free native C ABI candidate

The separate [Wasmtime v50.0.2 minimal C API candidate](../verification/wasmtime-c-api/README.md)
now pins the official patched upstream release commit
`f9a93b049a23bef9a33cfa3888f32af33a178749`, requests
`wasmtime-c-api --release --no-default-features`, rejects Cranelift/Winch
compiler features in Cargo's resolved feature tree, and writes a
build-receipt candidate with native library SHA-256 and Git identity.
Its shell syntax and no-argument fail-closed behavior were verified;
**the Rust/native library was not built** in this environment.

Do not swap a 50.0.2 `libwasmtime` binary under the 50.0.1 managed
binding: the exact C ABI and serialized module identity must match.
A compiled native library must also be tested from a real published
.NET Native AOT host. The existing 50.0.1 synthetic smoke remains test-only
and never authorizes untrusted tenant admission.

See [issue #566](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/566)
for the independent, private-source snapshot-specific CI carrier approval
request. No cross-org private tree copy has been approved or executed.

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
