# .NET experimental immutable fixed `Array<int>.get` milestone

**Status: DRAFT and NOT_EXECUTED until exact-head real compilation evidence.**

This is a deliberately small follow-up **stacked on Option<int> PR #428**, in the
opt-in `oresnet check-structured`/`compile-structured` pipeline. The shipping
scalar i64 CLI remains unchanged. It does not provide general collections,
Rust-style loans, actor heaps, ref32 graph lowering or a portable stdlib yet.

## Temporary closed value representation

```atto
pub fnc main():int {
  val Array<int> numbers = Array.of(10, 42);
  val Option<int> result = numbers.get(1);
  return result.unwrap_or(0);
}
```

- An `Array<int>` in this compiler slice is an **immutable fixed-size value**
  of **0–8** signed i64 scalar SSA definitions. It is not a CLR `T[]`,
  resizable `Vector<T>`, heap allocation or native array handle.
- The temporary `Array.of(...)` factory syntax makes array admission
  independent of direct bracket indexing. It is not a claim that this syntax
  is the final cross-backend AttoLang stdlib interface.
- `.get(index)` evaluates the signed i64 index once. Each value is selected
  by equality against an admitted constant element offset. A separate
  verifier opcode `ArrayIndexValidI64` proves `0 <= index < length`
  without integer subtraction, narrowing, guest pointer math, indexing a
  CLR array or invoking unchecked CIL `ldelem`.
- Valid results are represented using PR #428's separately typed `Bool`
  presence tag and `I64` payload. Absence is `None` for negative,
  empty, equal-to-length and oversized indexes. This does not flatten or
  implicitly unwrap `Option<int>`.
- `val`-only immutability is deliberate: no mutation, resize, `rt borrow`,
  `rt mut borrow`, array functions/closures, actor sends, or generic
  `Array<T>` ABI. Array values may be copied as already-verified immutable
  SSA components. At branches/loops their dominating scalar definitions
  remain valid and cannot be overwritten by a mutating alias.
- A bound of eight prevents dynamic SSA explosion from a single literal;
  all element expressions evaluate exactly once, left-to-right. A larger
  array is **rejected**, not silently allocated on the CLR heap.
- **Cross-backend Copy<int> subset:** the experimental compiler additionally
  accepts `array.get_copy(index)`, lowering it to the **same**
  `StructuredArrayGet` → checked `Option<int>` CFG/CIL path as `array.get(index)`.
  This is a spelling and semantic compatibility step with LLVM's
  `Array<int>.get_copy(int)` for the **immutable, closed scalar** intersection.
  No borrow or alias is returned. It does **not** implement LLVM's owned
  dynamically mutable array, `new Array<int>()`, `add`, `set`,
  ReadLoan, actor-owned native region, or native array transport.
  No general `Copy<T>` inference is enabled. The new exact-source fixture
  `contracts/collections/v1/array-get-copy-immutable42.atto` tests
  `Some(0)`, `Some(42)`, `None`, and the native executable's return 42.
  A bool-index fixture must be rejected with a compiler-owned type error.
- Unsafe `array[index]` remains disabled, aligned with hardening PR #418.
  A future semantic merge with #418 must preserve that denial and admit only
  separately typechecked collection construction and checked `.get()`.

## Security / future work

This is **not** Rust borrow checking or general managed array safety.
Future guest collection implementation must first establish:

1. Typechecked, monomorphized generic `Option<T>` and `Array<T>` with
   defined layout and no implicit nested `Option` flattening.
2. Actor-owned **unsigned 32-bit guest-relative offsets** with widened
   bounds, alignment, owner, tenant and generation verification before a
   64-bit native address can be derived. Invalid references must never load.
3. Static shared/exclusive loans, compile-time conflict rejection,
   use-after-`rt take` rejection, mutation/resize during loans forbidden,
   and no unchecked cross-actor/cross-suspension loan escape.
4. Atomic failure/rollback for allocation quota denial, zeroized retired
   slots and generation-fenced handle retirement. `critical` is a
   non-suspending checked root swap, not a replacement for borrow proofs.
5. A canonical guest-visible `.atto` core/std API and independent
   Java/GraalVM, LLVM, .NET, Wasm, Rust, Forge backend receipts.

## Evidence gate

The separate .NET 10 Linux/Windows workflow at
`.github/workflows/fixed-array-option-nativeaot.yml` must execute:

- 31 independent valid source → verified CFG → emitted CIL interpretation
  positive cases, 32 negative source cases, 11 forged CFG mutations, and
  diagnostic-exact bool-index rejection for `.get_copy`;
- exact Git-blob-verified `.atto` sources for present, absent and empty
  arrays, and denied mutable, direct indexed, over-budget forms;
- real source compiler and separately linked Linux and Windows NativeAOT
  guest binaries returning 42, strict native-layout verification and
  artifact digests, with **nonzero executed steps and exact PR head SHA**.

Source inspection, CI failures with `steps=null`, synthetic receipts and
test-only reference evaluation are **NOT_EXECUTED guest proof**. Keep draft
until the real compiler is executed and reviewed independently. Do not
mirror private source across organizations without explicit authorization.

Public name AttoLang; legacy `.ores`, Oreslang repository and `oresnet`
identities remain supported.
