# Oreslang .NET — Windows Native AOT backend

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

### Optional exact-SHA source-local Linux AOT execution

If hosted Actions cannot allocate runners, repository maintainers can use an
**isolated, ephemeral, source-repository** Linux x64 runner with the additional
label `oreslang-net-aot` **in the dedicated organization runner group
`oreslang-net-aot-trusted`**. Provision .NET SDK 10, clang, zlib headers,
binutils, `file`, `realpath`, and standard Unix tools. This does not require
a cross-organization source mirror or secrets.

**Runner group isolation is mandatory, not just the job-level `if` check.**
Before registering a runner, an organization administrator must create the
`oreslang-net-aot-trusted` runner group with **Selected repositories** set
only to `ores-truffle-oreslang/oreslang-source.net`, and **Selected workflows**
set only to
`ores-truffle-oreslang/oreslang-source.net/.github/workflows/strict-linux-native-aot.yml@refs/heads/main`.
The trusted workflow also verifies its own canonical `GITHUB_WORKFLOW_REF`
against that exact path before checkout. Dispatch concurrency is isolated
from normal push and PR runs, and a later manual dispatch cannot cancel an
in-flight, approved Native AOT certification; it queues instead.
Never register this worker in the default/all-workflows group. Otherwise a
separately authored pull-request workflow could target the same self-hosted
labels, bypassing this particular job's guard. Require an independent admin
read-back of repository + workflow access restrictions before connecting an
ephemeral, isolated runner. If these restrictions cannot be enforced, **do not
register the runner or run unreviewed source**: fix source-hosted admission or
use only a separately approved immutable private-source carrier instead.

**Never enable this runner on unreviewed code or while source-main is
unprotected.** The job refuses allocation unless GitHub reports
`github.ref_protected == true` on the default-branch dispatch and the exact
immutable source repository ID `1419704375`, and it checks both values again
before checkout. An unprotected branch cannot schedule this job even if
repository variables have been accidentally enabled. Independently review
and protect the source repository's
default `main` branch **before** enabling it. Set the non-secret GitHub
Actions repository variables `ORESLANG_NET_SELFHOSTED_LINUX=enabled`,
`ORESLANG_NET_SELFHOSTED_LINUX_SHA=<exact reviewed source commit SHA>`, and
`ORESLANG_NET_SELFHOSTED_LINUX_TREE=<its verified 40-hex root Git tree>`.
Then use **Run workflow** on this workflow's `main` revision. A
`pull_request` event can never allocate this self-hosted runner:
unreviewed PR-controlled workflow YAML must not be a runner authority.
The manually dispatched default-branch workflow first validates immutable
SHA/tree controls, then checks out the reviewed commit from this same repo
and validates the actual root tree **before executing source code**.

The workflow run's GitHub `head_sha` identifies the trusted `main` workflow
revision, **not** the tested feature commit; the receipt independently names
`workflow_sha`, `tested_source_head`, and `tested_root_tree`. A changed
feature commit or tree requires fresh independent review and reauthorization.
Use only isolated ephemeral runners, no privileged shared runners or injected
secrets, and unset all three variables/retire the runner after execution.

The job checks source head/tree, runs the managed suite, publishes actual
Native AOT compiler/app binaries, executes canonical 14/42/40 sources plus structured CFG/SSA source=45 and
fail-closed source negatives, and retains a bounded SHA-256 receipt. The
normal hosted jobs remain independent. A configured runner and actual
nonzero executed job steps are required; this draft does **not** assert
that the release has been compiled or tested.
