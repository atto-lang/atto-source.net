# Wasmtime C-ABI TenantVM candidate for .NET Native AOT

**Status: experimental, not shipping, unexecuted at this head.** The
`spikes/wasmtime-dotnet` project probes the official `Wasmtime` NuGet
package, pinned to 50.0.1. This is a possible alternative to the existing
`spikes/tenant-vm-dotnet` adapter for the shared LLVM native TenantVM.
Do **not** change `IsolationMode.NativeVmIsolate` to admit untrusted guests,
replace `OresVmHost`, or claim conformance until all release gates pass.

## Which ABI is involved?

```text
Atto/Oreslang source
  -> compiler HIR/MIR
      -> trusted target: .NET CIL -> .NET Native AOT host executable
      -> untrusted target: independently validated Wasm module/artifact
  -> host C# Wasmtime API (managed facade)
  -> .NET P/Invoke FFI over Wasmtime's stable C ABI
  -> native libwasmtime (Rust implementation)
  -> Wasm guest instance and its private linear memory
```

CIL (Common Intermediate Language), any proposed CIR, and compiler IR are
not foreign-function ABIs. The host invokes a managed method. The Wasmtime
binding calls native C functions, then Wasmtime runs compiled Wasm; the
guest-to-host boundary is **typed Wasm imports/exports**, NOT a direct CLR
reference or unmanaged pointer to the actor. This experiment intentionally
exposes **zero** host imports (including WASI) to untrusted modules.

The runtime has three distinct identities:
* An `Engine` may be shared for immutable code/configuration.
* A `Module` may be shared as immutable compiled code.
* Every tenant generation must have its own `Store`, instance, mutable
  memory, actor registry, grants and lifecycle. A store is single-entry;
  never invoke one store concurrently on multiple physical carriers.

## Run the bounded experiment

Requires a patched .NET 10 SDK, native toolchain for the selected RID,
the matching pinned Wasmtime native library from the NuGet RID package,
and network access for the initial trusted NuGet restore.

```sh
dotnet run --project spikes/wasmtime-dotnet/WasmtimeTenantSpike.csproj -c Release

# This MUST actually execute, not merely complete restore/build:
dotnet publish spikes/wasmtime-dotnet/WasmtimeTenantSpike.csproj \
  -c Release -r linux-x64 -o artifacts/wasmtime-tenant-spike/linux-x64
./artifacts/wasmtime-tenant-spike/linux-x64/WasmtimeTenantSpike
```

Also test `win-x64` with an actual Windows Native AOT runner. The first
command is a **managed runtime** smoke. The second is only a candidate
Native AOT host smoke: check the output for unsupported dynamic compilation,
reflection-based call paths and unmanaged library packaging. A passing
publish is not an isolation security certification.

Current probes verify intended handling of two independently mutable
stores, neighbor survival after OOB and fuel traps, retired generation
denial, fresh state on recreation, digest mismatch rejection and the
no-import policy. The fixture is handwritten WAT transformed to WebAssembly
inside the test host; it is **not** compiled Atto/Oreslang source.

## Non-negotiable release gates

1. **Strict full-AOT policy.** The released .NET compiler and host must
   remain Native AOT without runtime CIL/assembly loading. In addition, the
   guest pipeline must define whether Wasmtime's compilation of Wasm to
   native instructions at runtime is allowed. Under the current strict
   *no runtime JIT* requirement, it is **not** allowed. The fixture's
   `Module.FromBytes` compiles guest Wasm at runtime and therefore is
   explicitly **not** a shipping path. Build trusted precompiled artifacts
   ahead of time and authenticate their origin, target, Wasmtime version and
   module hash before use. Wasmtime deserialization is **unsafe for
   untrusted serialized native-code artifacts**; only host-produced and
   authenticated artifacts may enter such a loader.
2. **Enforced resource limits.** Implement native store-level memory,
   table, instance, stack, host-buffer and aggregate tenant ceilings.
   The fixture's `(memory 1 2)` is a test module bound, NOT a per-tenant
   host memory limiter. Fuel measures guest instructions, not elapsed time
   or arbitrary host callback work. Add epochs/interrupts, watchdog
   deadlines and reliable retirement with observed physical cleanup.
3. **Production admission.** Immutable, bounded module manifests;
   compiler-verified source-to-Wasm ABI; checked exported signatures;
   deny-by-default imports; no guest native CIL, FFI, `AssemblyLoadContext`,
   open file descriptors, network sockets or unrestricted WASI. Validate
   Wasm before instantiation, disallow module-provided host authority and
   charge host-side work to tenant quotas.
4. **Actor contract.** Generation-fence tenant handles, bound mailboxes,
   copied and versioned cross-tenant messages, no raw pointers or direct
   CLR object references, sealed actor state, non-reentrant actor turns,
   fair scheduling among tenants and cancellation/quiescence accounting.
5. **Fault tests.** Run malicious Wasm for OOB reads/writes, stack growth,
   `memory.grow`, allocation exhaustion, imports, denial of service,
   nested callbacks, forged/replayed capabilities, cross-tenant message
   confusion and endless loops. Demonstrate neighbor survival and
   no writable memory sharing. Add high churn and parallel tenants.
6. **ABI/RID/build trust.** Verify exact `Wasmtime` NuGet package version,
   native `libwasmtime` platform binary, signed provenance and license;
   test `linux-x64`, `linux-arm64`, `win-x64` as available.
   Pin exact Git tree and run real CI jobs with nonzero steps. A dependency
   restore alone is not evidence.
7. **Conformance.** Compare the candidate to
   `docs/TENANT_VM_NAMING_V1.md`, `docs/VM_ISOLATE_CONTRACT.md`
   and the shared TenantVM isolation-v2 cases. Promote the adapter only
   after independent review and real Native AOT receipt. Preserve the
   existing fail-closed production mode in the meantime.

## Sources

- Wasmtime C API and language integrations:
  https://docs.wasmtime.dev/lang.html
- .NET embedding: https://github.com/bytecodealliance/wasmtime-dotnet
- NuGet version: https://www.nuget.org/packages/Wasmtime
- Managed API fuel / epoch:
  https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.Config.html
- .NET Native AOT P/Invoke:
  https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop
