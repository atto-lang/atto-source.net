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

## Ownership
- `.net`: Native AOT host integration, Wasmtime Store ownership, admission and lifecycle.
- `atto-lang/atto-source.wasm`: Wasm guest build/ABI validation; no duplicate parser.
- `.llvm`: keep existing custom native TenantVM, no Wasmtime dependency.
- `.forge`: build independent native TenantVM analogous to LLVM; no Wasmtime dependency.

References: https://docs.wasmtime.dev/security.html , https://docs.wasmtime.dev/examples-pre-compiling-wasm.html , https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ .
