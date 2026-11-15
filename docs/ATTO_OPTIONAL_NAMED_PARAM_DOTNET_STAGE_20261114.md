# AttoLang `int?` and independent parameter presence — .NET structured slice

**Status: staged NativeAOT-friendly closed `Option<int>` type sugar; no optional-call ABI parity.**

Within the opt-in .NET structured compiler, `int?` is now a type-position alias
for `Option<int>`. A `val int? x=Some(42)` or `let int? x=None`
local uses the existing tag + i64 payload, typed CFG, verified CIL and
test-only emitted-CIL interpreter. It is **not** a host-nullable `System.Nullable<int>`,
nullable CLR reference, or implicit conversion from `int` to `Some(int)`.

This is an intentionally closed subset. `bool?`, `int??` and
`Option<int>?` fail before CIL emission because nested/boolean Option layout is
unproved here. `int? formal` still fails with `E_OPTION_ABI_UNSUPPORTED`
for method arguments and results, and `int name?` / `name? = default` are
rejected before a helper's parameter map is published: no skipped ABI slot,
source-order default evaluation or unverified overload selection.

Companion Java main-based parser/fail-closed draft:
[Java #3078](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/3078).
Canonical language contract:
[spec #51](https://github.com/ores-truffle-oreslang/attolang-language-spec.md/pull/51).

New regression sources in `OptionI64CompilerTests` exercise four
`int?` positive local programs through actual typed CFG and emitted-CIL
verification *when the suite executes*, plus rejected implicit conversions,
unproved nesting and omitted parameter signatures. Those tests are authored,
**not** verified by a stepful build in this PR.

Next: a full declaration-arity checked callable plan with named source-order
bindings, typed `None` omissions, immutable defaults, ownership/effects and
Native AOT guest execution receipts; then generic Option<T> beyond int.
