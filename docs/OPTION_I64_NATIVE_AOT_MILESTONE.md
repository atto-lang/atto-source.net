# .NET closed `Option<int>` → verified CFG/CIL milestone

Status: **draft, NativeAOT execution not yet verified**. This is a narrowly
typed, **allocation-free** source feature in the explicit
`oresnet check-structured` / `compile-structured` lane. The shipping
`check` / `compile` scalar frontend still rejects Option source.

## Precisely supported

```atto
pub fnc main():int {
  val Option<int> result = Some(42);
  if result.is_some() {
    return result.unwrap_or(0);
  }
  return 0;
}
```

- `Some(int)` / `None`, `Option<int>` local `val` and `let` bindings,
  reassignment through `let`, `.is_some()` and eager `.unwrap_or(int)`.
- `None` is not a special signed i64 sentinel. Each source value lowers to
  **two separately verified SSA IDs**: an exact Boolean tag and a full signed
  64-bit payload. Every signed i64, including 0, MIN, MAX, remains valid.
  An absent value has false tag and canonical zero payload.
- An intrinsic verifier opcode `OptionOrI64` accepts exactly (bool tag,
  i64 payload, i64 fallback), with dominance, source type and SSA checks
  independent of the parser. It emits explicit CIL branches selecting exactly
  one stored i64 result; it does not use reflection, `Nullable<T>`,
  heap allocation or runtime wrapper objects.
- Eager evaluation is deliberate: the fallback is evaluated **once**
  before selection even when the tag is true; do not translate this into
  lazy `unwrap_or_else` semantics.
- CFG joins and loops carry both tag and payload via independently typed
  block parameters. An `Option<int>` may not appear in a CIL local or
  MethodDef ABI without an explicit closed-layout proof.

## Explicit nonclaims / unsupported source

- This is **not** a general monomorphized `Option<T>`, generic stdlib,
  `Option<Option<int>>`, `Some(None)`, flow refinement, pattern matching,
  iterator, array access, borrow checker, actor memory or hot reload.
- Method parameters/results of type `Option<int>` reject until a stable,
  compiled, two-word/native ABI is designed; no implicit conversion to
  `bool` or `int`, no implicit flatten, no unchecked `.unwrap()`.
- `const Option<int>` is not supported without a tagged const evaluator.
  Array `a[i]` remains unsupported and gated in draft PR #418.
- Genuine actor-owned `Option` access still needs verified ref32 bounds,
  generation, tenant and actor memory fences, shared immutable snapshots
  and exclusive mutable loans; see issue #419 and actor-heap PRs.
- This branch stays separate from draft #66 (cross-backend flow contract),
  #258 (nested flattening rejection), and #421 (host actor shard reads).

## Evidence requirements

The actual tests include source → typed structured AST → verified CFG, the
independent CFG oracle, PE/CIL generation, deterministic binary bytes, a
verification-only CIL interpreter, malformed forged SSA negative tests, and
`.atto` positive and negative fixture files. The exact-head workflow builds
the real compiler and separately published Linux and Windows NativeAOT guests
which must return 42 for both Some and None cases. The compiler and guest
NativeAOT artifacts must be inspected and SHA-256 recorded.

**Do not count written tests as executed tests.** If a hosted job reports
`steps=[]` or `null`, its status is `NOT_EXECUTED`. No test run,
NativeAOT guest result, or release parity is claimed until actual
nonzero-step exact-head logs/artifact hashes exist and an independent
reviewer approves.

Public name: AttoLang. Preserve `oresnet`, `Ores.Net.*`, repository
names and legacy `.ores` compatibility.
