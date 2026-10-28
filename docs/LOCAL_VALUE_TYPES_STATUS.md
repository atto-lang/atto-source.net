# Local struct/record parity status (.NET)

Status: **experimental structured-frontend admission only**.

The .NET structured parser admits block-local value-type declarations:

```ores
fnc main(): int {
    struct Pair { int left; int right }
    record Status { bool ready, int code }
    return 42;
}
```

This follows the Oreslang distinction that local `struct` and `record` value
types may be declared inside callable bodies, while nominal `class` and
`actor` declarations remain non-local.

## Current guarantees

- `struct` and `record` remain distinct AST kinds.
- field declaration order is retained;
- this bounded slice accepts only explicit `int` and `bool` fields;
- field count is capped at 64;
- duplicate fields are rejected;
- an active local value-type name cannot be shadowed by a nested local
  value-type declaration;
- local `class` / `actor` declarations fail through a dedicated diagnostic;
- CFG/CIL lowering rejects every local value-type declaration through an
  explicit value-layout capability gate.

## Non-claims

This is **not** value-layout, constructor, field-access, function-signature,
serialization, ownership, CIL, or Native AOT parity.

In particular, the following remains unsupported in this slice:

```ores
fnc make_pair(): Pair {
    struct Pair { int left; int right }
    return Pair{1, 2};
}
```

Using a body-local type in the callable signature requires a separate
scope/hoisting design and verified ABI. Construction and field access likewise
require typed value-layout lowering before they may reach CIL.

A parser acceptance result must not be reported as executable backend parity.
Promotion requires checked construction/layout, verified CFG/CIL lowering, and
exact-head Native AOT execution evidence.
