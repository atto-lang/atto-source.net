# AttoLang .NET Native AOT backend

AttoLang is the current public language name (formerly Oreslang). This repository remains an independent CIL compiler plus .NET Native AOT prototype. Its implemented subset remains limited to checked signed-i64 scalar arithmetic; this branding update does not imply full-language parity. Keep existing `Ores.Net.*` project identities, `oresnet` command, generated artifact names and `.ores` fixtures stable.

Use [AttoLang Brand Compatibility v1](https://github.com/ores-truffle-oreslang/oreslang-common-source/blob/main/contracts/v1/attolang-brand-compat-v1.json) as the migration policy. Do not rename repository paths, package coordinates, command names, ABI or wire-format identifiers as part of a branding-only change.

## VM isolate execution architecture

A Native AOT-compatible `OresVmHost` now provides generation-fenced tenant handles, real actor mailbox ownership, host resource pins and shutdown admission; this is **not** CLR or OS process isolation, nor general compiled Oreslang guest execution.

See [the shared tenant IsolateVM contract](docs/VM_ISOLATE_CONTRACT.md) for lifecycle, V8 lessons, resource retirement, restrictions and remaining acceptance gates.


Independent **CIL compiler + .NET Native AOT runtime** for Oreslang. No LLVM, GraalVM, JIT, runtime CIL generation, or dynamic assembly loading is used by released binaries.

> **Status: bootstrap prototype, NOT the full Oreslang implementation.** Only a one-function checked signed-i64 arithmetic subset is implemented today. Native AOT compilation is required for distributed compiler and application artifacts.

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

Windows ARM64 is a planned target: script accepts `-Rid win-arm64`, but CI coverage and compatible Windows ARM64 build infrastructure remain to be established.

## Optional C# and F# source backends (experimental)

The verified scalar OresIR can also be emitted as C# (`emit-csharp`) or F#
(`emit-fsharp`) source, then compiled by Roslyn or the F# compiler into
CIL. These commands use the **same strict parser and verifier** as the
shipping `compile` command, preserve checked i64 operations, and never
introduce a runtime JIT dependency into the Native AOT release path.

~~~powershell
dotnet run --project compiler/Ores.Net.Compiler -c Release -- emit-csharp examples/hello.ores --out artifacts/OresProgram.cs
dotnet run --project compiler/Ores.Net.Compiler -c Release -- emit-fsharp examples/hello.ores --out artifacts/OresProgram.fs
./verification/assert-managed-source.ps1
~~~

This is source-generation tooling for the one-function arithmetic subset,
**not** full Oreslang-to-C#/F# support or a Native AOT release certificate.
See [managed source backends](docs/MANAGED_SOURCE_BACKENDS.md).

## Current language support

The compiler accepts exactly this shape with optional `define`/`pub` modifiers:

~~~ores
define pub fnc main() : int {
    return 2 + 3 * 4;
}
~~~

Features: decimal signed 64-bit literals (including `-9223372036854775808`), parentheses, unary plus/minus, signed addition/subtraction/multiplication and checked overflow. Source division is rejected until float result lowering exists: reference Oreslang `int / int` returns `float`, so emitting integer CIL division would silently change semantics. Explicit internal checked-i64 IR division remains a separate tested primitive. The supported syntax is a deliberate **subset** and not the definitive shared Oreslang grammar. No variables, actor syntax, imports, general functions, typechecker, borrow checker or async/await yet.

Static `select` is explicitly rejected by both the shipping scalar parser and the experimental structured parser before CFG/CIL lowering. This includes `select`, `do select`, `nb select`, `do nb select`, and `try select` with either `{ ... }` or `over ... end`. The rejection remains in place until .NET has checked `Channel<T>`/`Future<T>` arbitration, actor-lane continuation scheduling, ownership transfer, and cancellation semantics.

### Component map

| Directory | Purpose |
| --- | --- |
| `compiler/Ores.Net.Compiler.Core` | Lexer/parser/AST and direct System.Reflection.Metadata CIL emission |
| `compiler/Ores.Net.Compiler` | Native AOT-compiled CLI (`oresnet`) |
| `host/Ores.Net.NativeHost` | Statically references generated CIL and Native AOT publishes it |
| `runtime/Ores.Net.Runtime` | AOT-compatible runtime API, bounded mailbox, unmanaged actor arenas, and AoS/SoA/AoSoA shard substrate |
| `verification/Ores.Net.Tests` | Parser/CIL metadata/mailbox tests (Mono.Cecil is test-only) |
| `scripts/` | Windows-only Native AOT publish pipeline |
| `docs/` | Backend contracts and implementation roadmap |

## Advisory static analysis facts (v0)

Two **non-executing** CLI modes now export `ores-static-facts/v0` JSON with
SHA-256 identity of admitted UTF-8 source, a canonical `__root__.main/0`
procedure, and no unproved security or actor claims:

- `oresnet static-facts file.ores` uses the **shipping** scalar parser,
  frozen typed/verified OresIR, and the same accepted subset as `check`.
- `oresnet static-facts-structured file.ores` uses the **experimental,
  non-production** typed structured parser. Its `while` statements are
  marked `exit=unknown` and `safepoint=false`. This mode does **not**
  establish executable CIL or full Native AOT parity.

Both reject unsupported effects and malformed source instead of inventing
proofs. See [`.NET static facts v0`](docs/STATIC_FACTS_V0_NET.md) for
limits, trust boundaries, format and CI verification.

## Experimental structured CFG parity path

Alongside the shipping scalar compiler, the managed verification harness now
contains a non-shipping structured-source path for typed `int`/`bool` locals,
assignments, `if`/`else`, `while`, early returns, equality and signed
less-than. `OresStructuredCfgLowering` lowers that bounded AST into the
existing verified SSA CFG and deterministic CIL emitter; differential tests
compare the CFG reference evaluator with emitted CIL.

This narrows semantic parity with the JavaScript and LLVM/Wasm backends, but it
does **not** change the shipping `oresnet check/compile` grammar, prove
cross-backend source-syntax identity, or establish Native AOT execution for the
current head. Promotion to the production CLI requires an exact-head managed
and Native AOT receipt plus review of the structured grammar against the shared
Oreslang frontend contract.

### Experimental CLI for structured Native AOT source (draft)

This branch adds two explicitly opt-in commands, separate from shipping
`check` and `compile`:

```sh
oresnet check-structured examples/structured-sum45.ores
oresnet compile-structured examples/structured-sum45.ores --out artifacts/generated/OresGenerated.dll
```

The commands route the restricted typed `int`/`bool` structured AST through
`OresStructuredCfgLowering`, `OresCfgVerifier`, and deterministic CIL.
An additional bounded slice admits **previously declared, expression-only
typed helper functions** with static calls, at most 64 helpers and 16
parameters per helper. The experimental `compile-structured` path now
builds an independent **verified CFG for each helper** and emits a private
static CIL MethodDef for each one. Call sites evaluate typed arguments exactly
once, left-to-right, and emit direct `call` tokens to those MethodDefs.
The legacy inlining/reference-evaluator path remains separately available
for differential testing of expression-only functions; shipping `compile`
stays scalar-only. Acyclic typed calls are genuine CIL function calls, not
just inlined IR.

The next bounded slice extends helper bodies to checked typed locals,
assignments, `if`/`else`, `while`, and all-path return checking (including
`bool` returns), lowered into a *separate verified CFG/MethodDef per helper*.
Each emitted method gets its own explicit ordered Param metadata rows.
The pinned positive program is `examples/structured-function-bodies.ores`,
expected to return **42** after a helper loop and boolean helper call.
Windows/Linux workflow steps require real released Native AOT compiler and
application execution, not merely host-unit or parser checks.

Recursion, mutual recursion, forward references, dynamic dispatch, classes,
closures, generic instantiations and full ownership/actor-safe function frames
remain deliberately unsupported. Parser acceptance does not imply release
parity; source-org zero-step CI must be resolved before promotion.

Example (experimental only):

```ores
define pub fnc double(int value): int { return value * 2; }
define pub fnc main(): int { return double(21); }
```

```sh
oresnet check-structured examples/structured-functions.ores
oresnet compile-structured examples/structured-functions.ores --out artifacts/generated/OresGenerated.dll
```

They **do not** enable source actors, dynamic dispatch, imports, async I/O,
`select`, ownership syntax, floats or general function ABI support.
Normal `check` / `compile` remain scalar-only and reject structured source.

Windows and Linux workflows include a negative shipping-parser check and
real native application smoke tests returning 45 from a loop-carried sum and
42 from nested typed helper calls.
The C# verification harness additionally checks actual helper MethodDef
metadata, direct `call` and `ldarg` opcodes, deterministic assemblies, call
execution in a bounded verification-only CIL interpreter, and overflow traps
in the legacy verified SSA path. No runtime interpreter ships with Native AOT.

**Status: unverified draft.** Neither source-org zero-step GitHub Actions failures
nor reference-interpreter success prove current-head Native AOT execution.
No release or default-CLI promotion is allowed until exact-head Windows/Linux
Native AOT steps execute and independent compiler/security review passes.

### Experimental comparison spellings

On the **opt-in structured CFG/CIL path only**, integer comparisons accept the
canonical `eq` and `neq` operators and their `==` and `!=` aliases.
`ne` is not an operator. Comparisons are typed `int × int -> bool` and
emit verifiable CIL (`ceq` for equality, `ceq; ldc.i4.0; ceq` for inequality).
The production scalar compiler remains unchanged. Unimplemented bool-to-bool
comparison, other numeric types, actor frames and ownership-sensitive lowering
remain rejected until they have independent typed and Native AOT evidence.

## Verification

~~~powershell
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
./scripts/publish-windows.ps1 -Source examples/hello.ores -Rid win-x64
~~~

Unit tests use a managed development-time test harness; this is **not part of the shipped compiler or application**. CI must publish both deliverables to native executables and execute the resulting Windows binary. The absence of a `.runtimeconfig.json` is a supplemental check; the successful Native AOT publish is the decisive proof. CI status must be checked before claiming working Windows artifacts.

### Select runtime substrate

The runtime now includes a source-independent `SelectArbiter` primitive for the
future static-select lowering path. It provides exactly-one winner
linearization, late-arm loser revocation, cancellation handles, synchronous
conditional commit, cleanup-failure capture, and reentrancy rejection. It does
**not** enable source `select`, `Channel<T>`, Future selection, or actor
continuation scheduling; the compiler must continue to reject select syntax
until those layers are implemented and verified.

### Selectable i64 channel substrate

Stacked on the runtime `SelectArbiter`, the .NET runtime now has a bounded
`SelectableI64Channel` proof for the first scalar select profile. Readable and
writable readiness subscriptions are revocable, selected reads consume only
inside the arbiter conditional commit, selected writes publish only inside that
same transaction, and stale captured readiness notifications revalidate their
subscription identity before touching the buffer.

This remains runtime-only. It does not enable source `Channel<T>` or
`select`; generic typed channels, Future arms, close/EOF semantics, actor-lane
continuations, suspension and compiler lowering remain pending.

### Selectable i64 channel substrate

Stacked on the runtime `SelectArbiter`, the .NET runtime now has a bounded
`SelectableI64Channel` proof for the first scalar select profile. Readable and
writable readiness subscriptions are revocable, selected reads consume only
inside the arbiter conditional commit, selected writes publish only inside that
same transaction, and stale captured readiness notifications revalidate their
subscription identity before touching the buffer.

Close is also a terminal readiness event: buffered values drain before EOF,
an empty closed channel commits selectable EOF, and a selected write against a
closed channel terminates as closed without publishing. This remains
runtime-only. It does not enable source `Channel<T>` or `select`; generic typed
channels, Future arms, actor-lane continuations, suspension and compiler lowering
remain pending.

**Runtime limitations:** This remains a bootstrap compiler and single-process actor runtime. .NET Native AOT still includes managed GC and CoreCLR does **not** intrinsically provide independently collected per-actor GC heaps. Oreslang now provides an explicit bounded unmanaged `ActorArena` substrate with persistent actor-private bytes, turn-scoped scratch, aggregate native-memory budgeting, suspension-safe read loans, lifetime-only pins, generation fencing, quiescent zeroize/free, and actor-owned AoS/SoA/AoSoA shards. General guest object allocation, ownership/borrow syntax, async-frame lowering, shared-domain arenas, native collections, and zero-managed-allocation steady-state execution are still pending and must fail closed. Actor references carry an internal random nonce in addition to ID/generation; these are **not authenticated distributed capabilities**. No in-process hot-code reload can rely on `Assembly.Load`; full code replacement requires process-generation rollout. See [actor arenas and layouts](docs/ACTOR_ARENAS_AND_LAYOUTS.md).

See [architecture](docs/architecture.md), [roadmap](docs/roadmap.md), [CI recovery runbook](docs/ci-recovery.md), and [security and verification](verification/README.md). This repository's license remains unspecified and should be chosen by its maintainers.

See [value-boundary audit and admission tests](docs/VALUE_BOUNDARIES.md) for
aggregate, call, string, conditional and async feature limits.

## Engineering policy: no Python

Python is prohibited in this repository and throughout the `ores-truffle-oreslang` organization for scripts, tests, CI helpers, code generators, analyzers, automation, and host tooling. Existing Python functionality must be migrated to **Rust** without weakening behavior, security checks, fixtures, or test coverage. Do not add `.py`, `.pyi`, `.ipynb`, Python dependency manifests, `python`/`python3` commands, `pip`, `pytest`, `poetry`, `uv`, or `actions/setup-python`. The .NET compiler/backend remains .NET/C#; the Python-to-Rust mandate applies to Python tooling rather than rewriting language-specific implementation code.

## Strict Linux x64 Native AOT compiler and application

The Linux production path publishes **both** `oresnet` and its output
`oreslang-app` as independent, self-contained Native AOT executables.
Verified CIL is generated by the native compiler *at build time* and bound
through a static reference into the native application's AOT publish; it
is never dynamically loaded or distributed as a managed assembly.

With .NET 10 SDK, `clang`, `zlib1g-dev`, `file`, and `binutils` installed:

~~~bash
bash scripts/publish-linux.sh examples/hello.ores
./artifacts/compiler/linux-x64/oresnet check examples/hello.ores
./artifacts/apps/linux-x64/oreslang-app
# 14
~~~

`scripts/publish-linux.sh` deletes stale outputs, cleans the host between
different generated assemblies, refuses unsupported output locations, and
fails on Native AOT/trimming warnings. The Linux release layout verifier
requires an x64 ELF executable with no managed .dll or runtimeconfig/deps
sidecars. The separate strict Linux CI workflow runs both real native
executables, source-denial checks, and a two-source 14/42 isolation test;
its exact-head evidence is **unverified until Actions jobs run real steps**.

Native AOT does not imply zero GC or full Oreslang language parity. Actor
isolation, full async/structured execution, and general hot-reload remain
separately gated language/runtime milestones.


### Canonical three-source Native AOT parity gate

The strict Linux x64 workflow tests three **real Native AOT application
executions**: `hello.ores → 14`, shared `native.add → 42`, and shared
`native.precedence → 40`. The precedence source blob is pinned to
`2d09e697ad4eeac4ee263c3656b92998c67f0bfc`, the same immutable
fixture verified in the Windows Native AOT workflow. After publishing the
third fixture, the workflow re-runs *all three distinct binaries* to reject
stale-CIL or cross-source application contamination. It also verifies the
released native compiler refuses unsupported actor source both in
`check` and `compile` modes and leaves no rejected CIL file.

These are **CI requirements**, not evidence of their execution. The
repository's hosted Actions checks have returned zero executed steps; no
new exact-head Linux publish is certified until real step logs, native
artifacts, and independent review are available.

### Experimental structured source in Linux Native AOT

In the opt-in structured-compiler integration stack (#155 → #185), a fourth
release fixture exercises `while`, typed locals and assignments through
verified SSA CFG, metadata-only generated CIL and the linked Native AOT host:

~~~bash
bash scripts/publish-linux.sh examples/structured-sum45.ores artifacts/structured --structured
./artifacts/structured/linux-x64/oreslang-app
# 45
~~~

The ordinary `oresnet compile` path must **reject** that source without
emitting CIL. `compile-structured` is intentionally experimental; this
does not authorize async actors, ownership, imports or dynamic loading.
The strict Linux CI gate runs all four binaries after publishing the
structured fixture and requires expected outputs 14/42/40/45.

This integration branch is unverified until exact-head .NET 10 managed
tests and actual Linux/Windows Native AOT publish/execution run with
nonempty CI job steps. Its parents are the independent #185 structured
compiler and #200 Native AOT release branches.

## Untrusted actors and tenants (decided; not yet wired into this host)

**Decision (2026-11-03):** tenants, including hostile tenants and untrusted
guest code, share one OS process. Each tenant gets its own VM-isolate inside
it, as GraalVM isolates work. A per-tenant OS process or sandbox is not
required. It may be added as defense in depth, but never as a precondition.
Contract: [tenant isolation v2](https://github.com/ores-truffle-oreslang/oreslang-platform-conformance/pull/352).

On .NET this means:

- **Untrusted tenants run as validated Wasm on the shared native TenantVm
  core**, called through its C API (`spikes/tenant-vm-dotnet`). Guest code runs
  on native carrier threads, not CLR threads, so a spinning guest can be
  stopped, a guest fault ends only its VM, and tenants add no CLR GC load.
- **Untrusted CIL is refused.** CoreCLR has one process-wide GC heap, no
  runtime IL verification and no AppDomains, cannot kill a spinning managed
  thread (`Thread.Abort` is unsupported), and ends the process on stack
  overflow. An `AssemblyLoadContext` is not an isolate.
- **Direct CIL stays a first-class backend for trusted code**, JIT or
  NativeAOT. Trusted tenants may run as CIL; compiler-inserted fuel and depth
  checks can bound their loops and recursion.

Trust level and compilation mode stay independent: untrusted Wasm may be
JIT-compiled, AOT-lowered or interpreted. The **current shipping .NET
compiler/host remains NativeAOT only**, supports only its documented
arithmetic subset, and does not load CIL at runtime.

## Repository ownership and migration (2026-11-08)

**Current home:** `ores-truffle-oreslang/oreslang-source.net`. **Public language:** AttoLang (formerly Oreslang); retain legacy `oreslang-*` commands, packages, dependencies and existing `.ores` fixtures until each replacement is proven.

- **This repository owns:** .NET/CIL plus NativeAOT compiler, host ABI, actor isolation primitives and native target validation.
- **Out of scope:** JVM imports, JS transpilation or business cloud provisioning. Place shared language semantics and portable core/std in canonical `.atto` modules; keep only indispensable compiler, ABI and runtime-host primitives in this backend.
- **Organization boundary:** [`atto-lang`](https://github.com/atto-lang) is for language semantics, compilers, tooling, conformance and ecosystem libraries. [`atto-co`](https://github.com/atto-co) is for independently governed hosted products, deployment control, tenant infrastructure, identity and billing. Existing similarly named `atto-*` projects do not automatically share this repository's Git identity, code or test status.
- **First-stage migration proposal:** approved GitHub ownership transfer of this existing repo, preserving its name, numeric ID and history, to `atto-lang/oreslang-source.net`. No direct legacy-org → `atto-co` transfer is authorized; subsequent product carve-outs require new approval.

**Migration instructions (maintainer preflight):** coordinate exact source head and ownership in [issue #26](https://github.com/ores-truffle-oreslang/.github/issues/26), then follow private [runbook draft #25](https://github.com/ores-truffle-oreslang/.github/pull/25). Confirm source/target admins and independent security reviewer, target access and branch/ruleset protection, exact SHA/tree, PRs/releases, Actions, secrets, GitHub Apps, deployments, dependencies and compatibility links before transfer. Do not clone/mirror private source as a shortcut. Verify unchanged numeric ID and code tree after real transfer, restore controls, rerun exact-head CI and capture **nonzero executed steps**. `steps=[]` / `steps=null` is `NOT_EXECUTED`, not test success; host/static checks cannot certify real AttoLang guest execution, AOT or zero-GC. Defer repository/CLI/extension renames to separately reviewed changes.

## Experimental source actor/native arena integration

The opt-in `emit-actor-csharp` lane now emits a `SpawnRuntime` entry for its
bounded synchronous Counter profile. It uses the production ActorRuntime,
private native scalar state, execution-owned frames and supervisor mailbox
replies. The managed suite and a macOS arm64 NativeAOT probe execute ordinary
and overflow source-generated actors; the older trusted-host Counter API is
kept separate and its managed cells are not arena-isolation evidence.
See [execution evidence and open parity gates](docs/ACTOR_NATIVE_STATE_FRAMES_INTEGRATION_20261110.md).
This is not general actor CIL, concurrent-default actor execution or Spin support.
