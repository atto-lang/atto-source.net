# AttoLang `import dotnet` v0: checked BCL static call

**Status: experimental verified scalar CIL path; not general .NET/NuGet interop.**

The first bounded compiler-owned, explicitly opted-in .NET import family is:

```atto
import dotnet "System.Math" as Math;
pub fnc main():int { return Math.Abs(-42); }
```

The same allowlisted `System.Math` alias supports `Math.Min(a, b)` and
`Math.Max(a, b)` for exactly two typed `int` operands. These three static
methods can be nested with eager left-to-right evaluation.

Compile using `oresnet compile-structured <source.atto> --out OresGenerated.dll`.
This is deliberately separate from shipping `check`/`compile`, which still reject
host imports. The generated CIL references `System.Runtime` and its declared
`System.Math.Abs(Int64) -> Int64` static signature; it does not dynamically
load assemblies or use reflection. The pinned NativeAOT host links the output
statically. This is a **three-method, single-type BCL allowlist**; arbitrary packages,
constructors, instance methods, overload selection, generics, mutable objects,
`Task<T>`, callbacks and source-import graphs are NOT supported.

## What is gated

- The token-aware compiler parser accepts only one `System.Math` import with
  a local alias; all other host libraries and duplicate host imports fail closed.
- Only `Abs` with one typed AttoLang `int` / CIL `Int64` argument, and
  `Min`/`Max` with exactly two typed `int` operands can be used. Booleans,
  wrong arities, alias shadowing, missing imports and other APIs are rejected.
- A distinct typed, SSA-verified host opcode lowers to a deterministic CIL
  `MemberRef`. No ambient `System.Type`, runtime reflection or user-supplied
  DLL paths may reach the emitted module.
- A `long.MinValue` argument raises the .NET `Math.Abs` overflow exception;
  it must not silently wrap. Any future exception-to-AttoLang mapping requires
  an explicit ABI contract and executable negative fixture.
- Interop permission/capability, manifest-pinned NuGet resolution and AOT-safe
  cross-assembly ABI proof are prerequisites to admitting a broader surface.
  A statically linked trusted BCL scalar call does not grant untrusted tenants
  general .NET hosting.

## Native binary proof

```sh
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
bash scripts/publish-linux.sh examples/interop/dotnet-math-abs.atto artifacts/dotnetimport --structured
./artifacts/dotnetimport/linux-x64/oreslang-app
# expected: 42
```

`.github/workflows/dotnet-static-import-native-aot.yml` performs the
managed verifier, emits real compiler-owned CIL, publishes a native compiler
and native guest app under .NET 10, executes the ELF binary and emits SHAs.
No success is asserted until the **exact PR head** has nonzero executed steps
with source HEAD, hashes, job-attempt provenance, exit status and output.
`steps=null` / `steps=[]` means runner admission failure, not test proof.
The native CI workflow publishes two distinct `.atto` fixtures (simple Abs
and nested Min/Max/Abs), executes both native outputs and reruns the first
after the second publish to check for stale CIL contamination.

## Near-zero-GC / actor isolation contract

NativeAOT **still includes the .NET GC**. Borrow checking and separate
actor-owned unmanaged heaps can minimize GC for guest data, but do not make
external managed .NET libraries allocation-free or give per-actor CLR GC heaps.

1. Native actor payloads use bounded arena-relative **32-bit unsigned offsets**,
   not compressed object references, raw host pointers or cross-actor aliases.
   Generation/owner checks are required on every access and after suspension.
2. `rt borrow` and `rt mut borrow` are lexically scoped and must fail if a
   loan escapes its owner, enters a channel, survives invalid suspension, or
   is used after actor retirement. `rt take` revokes the previous owner.
3. Managed CLR objects are **foreign handles** with explicit capability,
   ownership, generation and retirement. They are not stored as 32-bit guest
   addresses. No managed object or callback may be silently sent to another
   actor or tenant. `critical` cannot authorize an unsafe host call.
4. Heap exhaustion, invalid offset, stack overflow, stale loans, cancellation,
   and host exceptions must fault only the affected actor/tenant where the
   isolation mode supports that claim. Untrusted CIL must remain denied.
5. Measure **per-turn managed allocations**, collection deltas, arena retained
   bytes, peak RSS, and retirement drain over long actor restarts. A host-only
   test or zero collections under Epsilon is not a portable AttoLang zero-GC
   proof. Report unknown counters as unknown.

This importer adds only a scalar BCL function and does **not** establish a
guest borrow checker, a physically isolated .NET heap, a zero-GC runtime, or
a complete NativeAOT execution receipt on its own.
