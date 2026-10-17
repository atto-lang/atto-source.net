# Refinement types: .NET parity status

**Status (2026-10-17): design/rejection parity only. No parser lowering, checked construction, runtime enforcement, or Native AOT refinement support is claimed.**

Canonical contract: https://github.com/ores-truffle-oreslang/oreslang-common-source/blob/design/refinement-types-three-syntaxes-20261017/docs/REFINEMENT_TYPES.md

The current .NET compiler remains an i64 bootstrap frontend. Until one normalized constraint model exists end-to-end, all proposed refinement surfaces must fail closed before CIL emission:

- constraint annotations such as `@Size`, `@Length`, `@NotEmpty`
- `type X = T with ...`
- `type X = T where ...`
- `define newtype X(T) ...`
- invalid/refinement-only Boolean spellings such as `with ... or ...`

The verification executable contains a positive scalar control plus five negative refinement admissions. A negative result is **not implementation parity**; it only proves that unsupported constraints are not silently erased into an unconstrained scalar value.

Future completion requires parser/typed-HIR support, Unicode-code-point length semantics, interval normalization, explicit checked `try_from`, structured `ConstraintError`, Zorde runtime descriptors, actor/serialization/FFI boundary validation, and Native AOT evidence.
