# Patched, compiler-free Wasmtime C API candidate (not shipping)

**Status: design + shell validation only; native build and ABI tests NOT_EXECUTED.**

This directory holds a narrowly scoped candidate for the untrusted
AttoLang/.NET **data-plane** native Wasmtime dependency. It is *not*
part of the released `Ores.Net.NativeHost` and does not change the
production `OresVmHost.WasmtimeSandbox` fail-closed behavior.

## Known-good upstream identity

Wasmtime upstream v50.0.2, released November 16, 2026:

- Public repository: https://github.com/bytecodealliance/wasmtime
- Release: https://github.com/bytecodealliance/wasmtime/releases/tag/v50.0.2
- Exact Git commit: `f9a93b049a23bef9a33cfa3888f32af33a178749`
- Important security fixes include GHSA-43j2-gcp4-m22g and
  GHSA-wvgq-g5hf-6gm6.
- Official minimal C API build:
  https://docs.wasmtime.dev/examples-minimal.html

The checked-in script `build_compiler_free_50_0_2.sh` accepts **only**
an existing, clean, public Wasmtime source checkout whose `HEAD` and
`v50.0.2` tag both resolve to that exact commit. It does **not** clone
anything, access private repositories, fetch credentials or push code.
It builds `wasmtime-c-api` with `--no-default-features` and
`--features disable-logging`, inspects the resolved feature tree for
Cranelift/Winch and emits an immutable build-receipt *candidate* with
upstream commit/tree and the resulting library SHA-256.

Run on a trusted Linux x64 native toolchain, with an **empty output path**:

```bash
bash verification/wasmtime-c-api/build_compiler_free_50_0_2.sh \
  /trusted/public/wasmtime-v50.0.2 /trusted/output/wasmtime-c-api
```

### Executed preflight and required native C API proof

Run the noncompiling preflight before any full Rust build:

```bash
bash verification/wasmtime-c-api/test_preflight.sh
```

It tests invalid arguments, non-Git inputs, wrong pinned source SHA,
nonempty output, and that no Cargo/Rust compiler runs on those failures.
The suite was **executed** locally and passed against Git blob
`1839bde32e2a3e9fe991e7df42065fbb5f91f60d` of the build script.

The full build now verifies the native library exports the C ABI
`wasmtime_module_deserialize`, `wasmtime_store_new`,
`wasmtime_instance_new`, and `wasmtime_context_set_fuel`,
while **refusing** a library that exports `wasmtime_module_new`,
the source-Wasm compilation entry point guarded by upstream's
`WASMTIME_FEATURE_COMPILER`. A separate C program
(`probe_store_abi.c`) links to the generated library, creates and
destroys two empty Wasmtime Stores, and emits
`WASMTIME_COMPILER_FREE_C_ABI_STORE_PASS` if successful.
The resulting ELF, symbol inventory and smoke log receive SHA-256
entries in the build receipt.

This is still not a deserialization or .NET Native AOT execution test.
Unsafe native `.cwasm` blobs are never passed to a Wasmtime
deserializer during this preflight. Valid precompiled artifacts require
a reviewed signed producer and matching engine/version/ABI.

A successful command is **not** sufficient to ship this native engine.
It does not certify the platform's C ABI, native code trap recovery,
Wasmtime resource limits, a validated guest manifest, or tenant isolation.

## Non-negotiable integration blockers

1. **Binding version match.** The existing `Wasmtime` NuGet 50.0.1
   embeds/native-links its own Wasmtime library. Do not replace that
   binary with a v50.0.2 `libwasmtime.so` by search path or filename
   alone. That could introduce an unchecked C ABI mismatch. Build or
   obtain a *reviewed .NET binding targeting the exact 50.0.2 C API*.
2. **Precompiled module compatibility.** The current synthetic builder
   produces 50.0.1 `.cwasm`. Wasmtime native serialized artifacts
   are not an ABI-independent portable format. Recompile the module
   using the same 50.0.2 engine, matching feature profile, CPU target,
   host target triple and library release; authenticate the resulting
   native code before deserialization.
3. **Compiler-free proof.** Build the release library without
   `cranelift` or `winch`. On an actual .NET Native AOT executable,
   verify trusted `.cwasm` deserialization succeeds, while a raw
   `.wasm` compilation request is rejected. Source inspection of
   C# alone is insufficient, as the NuGet-supplied native library
   could still contain compilers.
4. **Resource isolation.** Implement independently bounded Store
   memory/stack/fuel/deadline, aggregate host quotas, exact-generation
   revocation, brokered I/O and retired callback accounting. Two
   independent Stores do not automatically enforce all of these.
5. **Windows CET.** Hardware-trap fail-fast remains an upstream
   Windows/.NET risk; do not disable CET or enable untrusted Windows
   tenant admission based on Linux evidence.
6. **Security approvals.** Private Atto source cannot be mirrored to
   an alternate CI org until approved under
   `contracts/private-ci-security.v1.json`. This public upstream
   library build is a separate operation, **not a private-source
   carrier authorization**. See source PR #563 and approval request
   issue #566.

## Test evidence

`bash -n` and the no-argument exit-64 behavior were executed locally
against the exact blob committed on this branch. Cargo, Rust and .NET
SDKs are unavailable in the current environment; **no C API or Native
AOT executable was built, linked or run**.

The source-contract Python suite checks the upstream release SHA,
compiler-feature denial, clean checkout and build-receipt shape. That
suite is wired into source GitHub Actions, but the source org's jobs
currently fail with zero runner steps, not successful test executions.
