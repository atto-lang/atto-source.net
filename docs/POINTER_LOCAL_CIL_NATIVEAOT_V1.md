# .NET bounded local Pointer<int> CIL / NativeAOT profile (candidate)

## Semantics

The direct ECMA-335 Atto CIL compiler now has a deliberately restricted
lexical read-lease profile for the two exact AttoLang source programs.
A typed BorrowedLocalIntPlan verifies the local place, one compiler-minted
handle, one readonly primitive copy, exactly one explicit unref and an
ordinary scalar return. The nonescaping pointer is then stack-promoted
into normal optimized i64 CIL: no user-addressable raw native pointer,
heap object, GC ownership or unsafe C# guest code is manufactured.

Recognized spellings:

```atto
pub fnc main(): int {
  let value: int = 42;
  val Pointer<int> ptr = rt ref value;
  val answer = rt deref ptr;
  rt unref ptr;
  return answer;
}
```

```atto
pub fnc main(): int {
  let value: int = 42;
  val Pointer<int> ptr = rt ref value;
  val answer = deref! ptr;
  unref! ptr;
  return answer;
}
```

Contiguous ref!value / deref!ptr / unref!ptr are also recognized; a
separated bang is explicitly rejected. The compiler's normal source
lexer strips comments and records token offsets before any authority
decision. A nominal Pointer<int> binding with an unsupported operand,
write, cast, branch, extra function or escaping handle fails before
any CIL metadata or output DLL is generated.

## Test requirements

scripts/test-cil-guest.sh compiles the exact three positive sources
into AttoGuest.dll and executes each in CoreCLR and Linux .NET NativeAOT
using the existing fixed C# process bridge. The 19 negative .atto
fixtures must reject with E_POINTER_* diagnostic and NO CIL artifact.
This is in addition to the older plain scalar and call-graph tests.

The scope is a one-place, one-read, one-release synchronous main.
General Pointer<T>, allocator-owned regions, multi-reader handles,
mutability, actor/TenantVM crossing, async, exceptions, cancellation,
FFI, and source-level pointer transport remain unsupported.

**Evidence status: authored, not executed.** Only a runner that
actually compiles the exact head and executes both CoreCLR and NativeAOT
guests may certify this profile. Source-only files and zero-step Actions
failures are NOT guest evidence. Keep draft pending material results.
