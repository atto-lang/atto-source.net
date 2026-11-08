# AttoLang .NET host imports: closed-world Math.Abs / Math.Max v0

Status: **compiler code and executed-test workflow authored, no stepful exact-head
compiler or Native AOT execution yet.** This is NOT general CLR import parity.

```atto
import dotnet "System.Math" as Math;
pub fnc main() -> int { return Math.Max(Math.Abs(-42), 10); }
```

Only two exact scalar signatures are admitted:
`System.Math.Abs(System.Int64) -> System.Int64` and
`System.Math.Max(System.Int64, System.Int64) -> System.Int64`.
All other types, overloads, allocations, instances, mutable collections,
reflection, unsafe pointers and network/file/process host APIs fail closed.

## Explicit build authority

No CLR host import is authorized by default. The parser and the independently
frozen SSA verifier both require a trusted `DotnetHostImportPolicy.MathI64V0`.
From the CLI, supply the explicit, narrow grant:

```sh
oresnet check examples/hostinterop-math-max.atto --capability interop.dotnet.math-i64
oresnet compile examples/hostinterop-math-max.atto --out artifacts/generated/OresGenerated.dll --capability interop.dotnet.math-i64
bash scripts/publish-linux.sh examples/hostinterop-math-max.atto artifacts/dotnet-host-max --dotnet-math
```

On Windows:

```powershell
./scripts/publish-windows.ps1 -Source examples/hostinterop-math-max.atto -Rid win-x64 -Output artifacts/dotnet-host-max -DotnetMath
```

Without the flag, source checking and compilation reject the import even when
the library/type string is spelled correctly. It is the responsibility of
the trusted build invoker to authenticate which source units may be built with
this capability. It does not grant runtime file, network or reflection access.
The permit cannot be supplied to `check-structured`, `compile-modules`,
`static-facts` or managed-source emitter modes.

## Compiler and codegen contract

1. Lexer/reader accept precisely one unescaped literal `"System.Math"`
   and an alias. Host type authority never comes from arbitrary user paths.
2. The typed AST `DotnetMathAbsI64` / `DotnetMathMaxI64` lowers to immutable
   typed SSA. Both parser and verifier demand an explicit policy grant.
3. Exact reference signatures are emitted as static ECMA-335 MemberRef CIL
   calls, against the pinned .NET 10 `System.Runtime` AssemblyRef.
   No `Assembly.Load`, `Reflection.Emit`, runtime lookup or JIT fallback.
4. The deterministic MVID hashes host method identities in addition to
   instruction bytes and local signatures; identical input yields identical PE.
5. `StaticFactsV0Exporter` rejects both host-call opcodes rather than
   silently reporting zero external call edges. C#/F# source emission
   rejects unsupported foreign calls, rather than reinterpreting them.
6. Linux and Windows Native AOT workflows check unauthorized compilation
   produces no generated DLL, Math authorization admits only pure scalar
   operations, and native artifacts really execute to output `42`.
7. The `.atto` and legacy `.ores` frontends keep their established commands
   and identities. Portable core/std remains canonical `.atto`, not C# policy.

## Safety and production boundaries

Both allowed calls accept and return an i64 scalar. They do not transfer host
references, heap allocations, actor ownership, callbacks or handles.
`Abs(Int64.MinValue)` throws on CLR overflow; full guest exception/effect
parity requires separate work. Arbitrary .NET library imports may allocate on
the GC heap and MUST NOT be called zero-GC or automatically actor-isolated.
A future opaque CLR handle design must enforce per-actor generation fencing,
borrow checking, deterministic resource cleanup and Sendable restrictions.

General library imports, third-party package assemblies, nullable/optional
mapping, overload resolution, generics, async Task adapters and Native AOT
reachability auditing are tracked in
[issue #424](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/424).

Evidence: `verification/Ores.Net.Tests/DotnetHostImportTests.cs`,
`examples/hostinterop-math-abs.atto`,
`examples/hostinterop-math-max.atto`, and strict Linux/Windows workflows.
No job with `steps=[]` or `steps=null` is an executed compiler test.
