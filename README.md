# Oreslang .NET — Windows Native AOT backend

Independent **CIL compiler + .NET Native AOT runtime** for Oreslang. No LLVM, GraalVM, JIT, runtime CIL generation, or dynamic assembly loading is used by released binaries.

> **Status: bootstrap prototype, NOT the full Oreslang implementation.** Two narrow source surfaces are implemented as candidates: checked signed-i64 `main() -> i64`, and an explicit provider-neutral `String -> String` Lambda subset. Native AOT compilation is required for distributed compiler and application artifacts, and the current exact-head Actions jobs still fail before runner allocation.

## Native AOT pipeline

```text
Oreslang source (.ores)
    -> frontend (lexer, parser, AST)
    -> System.Reflection.Metadata CIL emission: OresGenerated.dll
    -> compile-time static reference from Ores.Net.NativeHost
    -> dotnet publish -c Release -r win-x64 (Native AOT)
    -> standalone oreslang-app.exe
```

Unlike managed .NET publishing, the final executable is compiled ahead of time by the .NET toolchain and does not require an installed .NET runtime. Dynamic compilation and JIT fallback are **not** permitted. For Windows Native AOT install the .NET 10 SDK and Visual Studio C++ build tools / Desktop development with C++ workload.

## Native build on Windows

~~~powershell
./scripts/publish-windows.ps1 -Source examples/hello.ores -Rid win-x64
./scripts/publish-lambda-windows.ps1 -Source examples/lambda-ping.ores -Rid win-x64
./artifacts/apps/win-x64/oreslang-app.exe
# 14
~~~

This script **first publishes the compiler itself using Native AOT**, runs the native compiler to generate `OresGenerated.dll`, then publishes the statically linked application via Native AOT. The generated DLL is a build artifact, **not** dynamically loaded at runtime.

Manual version:

~~~powershell
dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r win-x64 -o artifacts/compiler/win-x64
./artifacts/compiler/win-x64/oresnet.exe compile examples/hello.ores --out artifacts/generated/OresGenerated.dll
$generated = (Resolve-Path artifacts/generated/OresGenerated.dll).Path
dotnet publish host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj -c Release -r win-x64 -o artifacts/apps/win-x64 "-p:GeneratedAssembly=$generated"
./artifacts/apps/win-x64/oreslang-app.exe
~~~

A separate strict Lambda pipeline uses `compile-lambda` plus the statically
linked `Ores.Net.LambdaNativeHost`:

~~~powershell
./scripts/publish-lambda-windows.ps1 -Source examples/lambda-ping.ores -Rid win-x64
./artifacts/lambda-apps/win-x64/oreslang-lambda-app.exe --invoke ping
# pong
~~~

The Lambda candidate emits `Oreslang.Generated.OresProgram.Invoke(String) -> String`
and never dynamically loads generated CIL. CI is wired to exercise both direct
`ping -> pong` and the real ORW1 worker protocol when a runner is allocated.

Windows ARM64 is a planned target: scripts accept `-Rid win-arm64`, but CI coverage and compatible Windows ARM64 build infrastructure remain to be established.

## Current language support

The compiler accepts exactly this shape with optional `define`/`pub` modifiers:

~~~ores
define pub fnc main() : int {
    return 2 + 3 * 4;
}
~~~

Features: decimal signed 64-bit literals (including `-9223372036854775808`), parentheses, unary plus/minus, signed addition/subtraction/multiplication and checked overflow. Source division is rejected until float result lowering exists: reference Oreslang `int / int` returns `float`, so emitting integer CIL division would silently change semantics. Explicit internal checked-i64 IR division remains a separate tested primitive.

The explicit Lambda compiler mode additionally admits the existing provider-neutral
handler shape:

~~~ores
define module handler as
  pub fnc invoke(String event): String {
    if event == "ping"; then return "pong"; fi
    return "unsupported";
  }
end
~~~

It currently supports string input, bounded literal equality guards, and string
returns only. This is a deliberately narrow source subset, not general function,
JSON, class, actor, closure or collection parity. Unsupported syntax fails closed.

### Component map

| Directory | Purpose |
| --- | --- |
| `compiler/Ores.Net.Compiler.Core` | Lexer/parser/AST and direct System.Reflection.Metadata CIL emission |
| `compiler/Ores.Net.Compiler` | Native AOT-compiled CLI (`oresnet`) |
| `host/Ores.Net.NativeHost` | Statically references scalar generated CIL and Native AOT publishes it |
| `host/Ores.Net.LambdaNativeHost` | Statically references generated `Invoke(String)` Lambda CIL and publishes the ORW1 worker |
| `runtime/Ores.Net.Runtime` | AOT-compatible runtime API, bounded byte mailbox prototype |
| `verification/Ores.Net.Tests` | Parser/CIL metadata/mailbox tests (Mono.Cecil is test-only) |
| `scripts/` | Windows-only Native AOT publish pipeline |
| `docs/` | Backend contracts and implementation roadmap |

## Isolation model

The .NET backend can support strong isolation, but **not through
`AssemblyLoadContext` as a security sandbox**.

Production profiles are now admitted explicitly:

- trusted code may execute in the same statically linked NativeAOT process;
- semi-trusted code uses a separate NativeAOT worker process and bounded IPC;
- untrusted NativeAOT code additionally requires OS-level capability/resource
  sandboxing;
- hostile portable code should use a separately produced `wasm32-wasi`
  artifact in a WASM host;
- collectible `AssemblyLoadContext` is development-only code/version
  isolation and is rejected by strict NativeAOT release admission.

The machine contract is
[`contracts/v1/dotnet-isolation-v1.json`](contracts/v1/dotnet-isolation-v1.json)
and the runtime admission API is
[`IsolationPolicy`](runtime/Ores.Net.Runtime/IsolationPolicy.cs).

This keeps hot code loading consistent with the strict AOT architecture:
compile a new generation, launch a new worker process, health/admission check it,
switch routing, drain the previous generation, then terminate it.

See [the .NET isolation model](docs/DOTNET_ISOLATION.md).

## NativeAOT worker mode

The statically linked application now has a candidate process-worker mode:

~~~text
oreslang-app --worker
~~~

It speaks the versioned `ORW1` bounded stdio protocol from
[`contracts/v1/dotnet-worker-v1.json`](contracts/v1/dotnet-worker-v1.json).
Frames are strict UTF-8, capped at 1 MiB, and invocation deadlines are capped at
30 seconds. The original scalar host still accepts only empty payloads for
`main() -> i64`. The separate Lambda candidate accepts normalized raw UTF-8
request payloads through `WorkerRuntime.RunText` and returns strict UTF-8
responses, with null/invalid/oversized responses failing opaque-and-retire.

The generic launcher clears inherited environment state and refuses untrusted
workers or scoped capabilities. Those require a verified OS-sandbox host
adapter; no process/container security claim is inferred from the framing
protocol itself.

## Verification

~~~powershell
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
./scripts/publish-windows.ps1 -Source examples/hello.ores -Rid win-x64
~~~

Unit tests use a managed development-time test harness; this is **not part of the shipped compiler or application**. CI must publish both deliverables to native executables and execute the resulting Windows binary. The absence of a `.runtimeconfig.json` is a supplemental check; the successful Native AOT publish is the decisive proof. CI status must be checked before claiming working Windows artifacts.

**Runtime limitations:** This is a single-process actor mailbox prototype. .NET Native AOT still includes managed GC; it does **not** intrinsically provide per-actor heaps or a zero-GC runtime. Actor references now carry an internal random nonce in addition to ID/generation, preventing accidental local same-ID spoofing. These are **not authenticated distributed capabilities**, and managed Native AOT still does not provide per-actor private heaps. No in-process hot-code reload can rely on `Assembly.Load`; full code replacement requires process-generation rollout.

See [architecture](docs/architecture.md), [roadmap](docs/roadmap.md), [CI recovery runbook](docs/ci-recovery.md), and [security and verification](verification/README.md). This repository's license remains unspecified and should be chosen by its maintainers.

See [value-boundary audit and admission tests](docs/VALUE_BOUNDARIES.md) for
aggregate, call, string, conditional and async feature limits.
