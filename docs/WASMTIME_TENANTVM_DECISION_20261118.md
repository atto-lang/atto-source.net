# Decision 2026-11-18: Wasmtime for untrusted .NET TenantVM guests

## Status
**Selected architecture, implementation gated / NOT_EXECUTED.** The checked-in `OresVmHost` is still a Native AOT-compatible *trusted* logical tenant host. The `WasmtimeSandbox` mode refuses guest admission until real engine-backed execution, quotas and teardown are wired and tested. This decision does not turn the CLR or `ActorArena` into a sandbox.

## Execution policy
| Guest | Allowed host | Security boundary |
| --- | --- | --- |
| Trusted, statically linked generated CIL | .NET Native AOT `TrustedInProcess` | Trusted-only; logical actor and tenant accounting |
| Untrusted, validated `.wasm` | `WasmtimeSandbox` (future) | Wasmtime module validation, independent tenant `Store` and instance/memory, capability-denied imports |
| Untrusted CIL, dynamic .NET assemblies or native machine code | **DENY** | Never fall back to trusted host, `AssemblyLoadContext`, raw pointers or old native TenantVM spike |
| Legacy `NativeVmIsolate` mode | **DENY** pending explicit migration | Compatibility enum only, NOT a supported sandbox |

This supersedes the **proposed .NET untrusted execution path** through the LLVM-owned `libtenantvm` native spike. Retain `spikes/tenant-vm-dotnet` as historical test evidence and do not remove its files or change the LLVM backend. Do not silently alias `NativeVmIsolate` to `WasmtimeSandbox`: old callers must receive a clear rejection and explicitly opt in.

The host may remain one .NET Native AOT OS process containing many tenant Wasmtime instances, with shared immutable compiled modules and separate tenant mutable state. A Wasmtime Store is an execution/resource ownership unit; it is not an OS process or a proof that arbitrary native host bugs are contained. Never expose native or FFI pointers to untrusted guests.

## Native AOT is non-negotiable
The **.NET host** must publish and execute as Native AOT for the supported Windows/Linux runtime IDs, with no dynamic assembly load, dynamic CIL generation, collectible AssemblyLoadContext or runtime reflection dependency. Validate the selected `wasmtime-dotnet` package (or C ABI + source-generated P/Invoke) *by compiling and executing the actual published Native AOT binary*; NuGet restore is not evidence of compatibility.

Wasmtime itself normally compiles Wasm to native instructions at runtime. If the release contract also forbids any runtime guest compilation, use a separate trusted, pinned precompilation pipeline and compatible Wasmtime precompiled artifacts. Treat serialized Wasmtime native code as **trusted executable code, not untrusted validated Wasm bytes**: verify provenance, version/target/CPU compatibility and authenticity before loading. A raw SHA digest of attacker-supplied `.cwasm` is not authorization. Fail closed on mismatch, unsupported target, absent signature or unavailable safe precompiled-loading path. Building the .NET host with Native AOT alone does not establish strict AOT for guests.

## Provider architecture
1. Keep `OresVmHost` as root admission, supervision, policy and actor-accounting authority, without giving guests its CLR object graph.
2. Add a native Wasmtime engine provider behind an explicit `WasmtimeSandbox` host interface. Reuse immutable verified modules only between compatible tenants; allocate independent Stores and non-shared linear memories for mutually untrusted tenants.
3. Admit only a versioned compiled Atto/Ores Wasm guest ABI; statically validate imports, exports, memories, tables and feature flags against an exact manifest. Default to **zero WASI imports**; any future file/network/time capability requires a separately verified host broker.
4. Bind actor/tenant identity to host-issued generation-fenced handles. Messages cross tenant boundaries only as validated, bounded, copied data or explicitly checked transfers; no raw buffers, global mutable guest data, function pointers or CLR delegates cross.
5. Enforce tenant maximums for memory/heap/stack, fuel or epoch deadlines, mailbox and I/O quota and host aggregate budget. Interruption does not prove physical teardown; retain active-call pins until callbacks and OS resources retire.
6. A trap, infinite loop, OOM, stale handle or host cancellation must stop the guilty tenant, revoke its capabilities and leave a neighboring tenant operational. A crashed *host process* remains a shared failure domain.
7. Guest re-creation always makes a fresh Store/instance/memory, tenant generation, actor registry, quota set and local symbols. No post-shutdown guest reuse.

## Required acceptance gates (NOT DONE)
- Real Native AOT `dotnet publish` + execution for Linux x64/arm64 and Windows x64 (and arm64 when supported), with the selected pinned Wasmtime binding and native library deployment.
- Two **actually compiled** source guests in distinct Stores, isolation of globals/memory, output values and concurrent neighbor progress. Tests of hand-authored synthetic Wasm are supplementary only.
- Malicious and malformed Wasm/imports, forged tenant/actor handles, stale generations, quota exceed, infinite loops, traps, memory bounds and restart; all must fail closed with no fallback to trusted CIL or `libtenantvm`.
- Native AOT host and guest artifact provenance and compatibility evidence at exact Git SHA, nonempty executed CI steps and independently reviewed boundary behavior.
- If end-to-end no-JIT AOT is required, independent proof of authenticated precompiled guest artifacts and a compiler-free runtime path.


## Implemented opt-in Native AOT engine probe (still NOT production)

`verification/Ores.Net.WasmtimeProbe` now references the **exact**
`Wasmtime 50.0.1` NuGet version, outside the shipping CIL/runtime host.
It uses a real Wasmtime `Engine`, one shared immutable synthetic WAT
`Module`, and two distinct `Store`s with private mutable globals and
non-shared one-page Wasm32 linear memories. The probe rejects any imports,
disables memory64/multi-memory/threads, enables fuel and stack limits, and
exercises out-of-bounds traps, infinite-loop fuel exhaustion, fresh store
creation and a surviving neighboring store. WAT is a synthetic **host probe**;
the tests DO NOT prove real `.atto` guest compiler/ABI parity. The source
implementation does NOT route through `OresVmHost`.

On Linux x64 the exact published Native AOT binary, **not** `dotnet run`,
is tested by `.github/workflows/wasmtime-nativeaot-probe.yml`:

```sh
dotnet publish verification/Ores.Net.WasmtimeProbe/Ores.Net.WasmtimeProbe.csproj \
  -c Release -r linux-x64 -o /tmp/wasmtime-probe -p:PublishAot=true
/tmp/wasmtime-probe/oresnet-wasmtime-probe
```

**Status:** source committed, executable Native AOT CI not yet verified.
No .NET SDK available in the author's local execution environment.

### Windows x64 CET blocker: FAIL CLOSED

As of this decision, upstream
[wasmtime-dotnet #374](https://github.com/bytecodealliance/wasmtime-dotnet/issues/374)
remains **open**. Its report documents a .NET 9+/10 Windows x64
CET-shadow-stack conflict where hardware-signaled Wasm traps can *fail-fast
the entire host process*, instead of terminating one guest. This violates
our fundamental neighbor-survival rule. The probe therefore exits **78**
on **all Windows** and untrusted `OresVmHost` admission remains disabled.
Do **not** disable CET for production. Require an upstream fix, an
independently reviewed mitigation preserving hardening, and a real Windows
Native AOT test that creates two stores, deliberately trips OOB/unreachable,
and proves the sibling survives before enabling Windows tenant execution.
Tests using `dotnet.exe` instead of the published `.exe` are insufficient.

## Generation-fenced multi-tenant Wasmtime lifecycle slice (2026-11-18)

The opt-in Native AOT probe now has a real `TenantStoreRegistry` implemented in
`verification/Ores.Net.WasmtimeProbe/TenantStoreRegistry.cs`. It is deliberately
restricted to a **trusted synthetic probe module** and does not make the
production `OresVmHost.CreateIsolate(WasmtimeSandbox)` admission available.

The registry creates independent Wasmtime `Store`/instance pairs, one fixed
one-page private memory and a fuel budget per tenant, with a root max-tenant
quota. A host-issued handle has a monotonically increasing generation and
issuer identity; foreign/replayed/destroyed handles fail. Guest entry is
serialized **per Store** rather than globally, and pins active calls so
quiescence cannot free an executing Store. Runtime guest exceptions revoke
only that tenant, then explicit drain/destroy disposes its Store. New tenant
incarnations get fresh memory and globals. Zero imports remain mandatory.
Windows execution and CoreCLR JIT-host execution reject inside the registry
itself; the Windows CET blocker is not merely a command-line test check.

`Program.cs` uses genuine Wasmtime instances to test root quota, separate
memory/globals, cross-registry handle rejection, trap and fuel shutdown,
neighbor survival, and fresh generation after restart. These are **synthetic
WAT tests, not .atto source-compiled actor tests**.

**Not yet done:** published Native AOT binary pass; integration with the
production `OresVmHost` actor/mailbox/capability broker; exact guest import/
export artifact attestation, aggregate native memory/stack budgets, real
Atto-generated guest ABI, host I/O teardown and the Windows CET recovery
problem. Production untrusted admission intentionally remains disabled.

## Consolidation of the second C-ABI spike (2026-11-18)

PR #564 contained a second opt-in `spikes/wasmtime-dotnet` test program.
The reusable security checks have been consolidated into this PR's
`verification/Ores.Net.WasmtimeProbe`, which already has the more complete
`TenantStoreRegistry` lifecycle and Linux Native AOT CI workflow. Do not
ship or maintain two competing Wasmtime host adapters.

The consolidated probe now has `TrustedProbeModuleAdmission`, which
requires the synthetic Wasm fixture to have a valid binary header/version,
a bounded size and an exact SHA-256 digest, using constant-time comparison.
It refuses imports even when the checksum matches. The executable tests cover
digest substitution, malformed header, empty and oversized input, denied
imports, duplicate/over-quota tenant admission and a stale handle after
reincarnation, in addition to memory, globals, trap and fuel isolation.

**Security limitation:** the expected checksum is computed from test-owned
WAT at runtime. It demonstrates an integrity check, **not authentication**.
Real tenants cannot supply their own approved digest, signed Wasmtime native
code, WAT source or CLR assemblies. A production build/release service must
sign and review a versioned manifest binding Wasm identity, imports/exports,
target architecture, Wasmtime version and provenance before any native
precompiled artifact may be loaded. `Module.FromBytes` compiles guest
code at runtime and is therefore strictly confined to the synthetic probe;
full no-JIT guest AOT is an unimplemented shipping release gate.

The Wasmtime probe workflow now pins checkout and setup-dotnet actions to
full reviewed SHAs, disables persisted checkout credentials, and asserts
that the checked-out Git SHA matches the pull request head. The publish
and executable smoke still require actual runner steps; GitHub Actions
failure with an empty `steps` collection is **NOT_EXECUTED**, not a
compiler or guest-test failure.

## Ownership
- `.net`: Native AOT host integration, Wasmtime Store ownership, admission and lifecycle.
- `atto-lang/atto-source.wasm`: Wasm guest build/ABI validation; no duplicate parser.
- `.llvm`: keep existing custom native TenantVM, no Wasmtime dependency.
- `.forge`: build independent native TenantVM analogous to LLVM; no Wasmtime dependency.

References: https://docs.wasmtime.dev/security.html , https://docs.wasmtime.dev/examples-pre-compiling-wasm.html , https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ .
