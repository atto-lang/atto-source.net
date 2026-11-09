# Native AttoLang collection factory frontier — .NET

Status: **DRAFT; source changes authored, exact-head .NET build/CIL tests not yet executed; NativeAOT execution not certified.**

## Implemented source-native candidate

The shipping scalar `OresParser` frontend now admits:

```atto
pub fnc main(): int {
    return Tuple.of<int,int>(11, 23)[1] + 19;
}
```

Also admits `Tuple.of(11,23)[1]` and `~tuple(11,23)[1]`.
This is a *closed, escaping-free scalarized tuple projection*, not a first-class
heap/aggregate value. The parser recognizes the actual AttoLang tokens,
checks that every element is a pure, signed i64 literal, validates optional
explicit `<int,...>` witness arity (up to 32), checks a direct
nonnegative constant index and exact bounds, and emits the selected checked
i64 scalar to the existing native OresIR/CIL path.

All arguments are pure checked literals, so no arguments with side effects,
throws, overflow, or move/borrow capability can be discarded during
scalarization. The scalarized value cannot escape into a local, actor, channel,
returning tuple, or serializer. `Tuple.of(...)` without an immediately
known projection must reject with `E_TUPLE_ESCAPE_UNSUPPORTED`.

The structured CFG frontend rejects factory expressions with
`E_COLLECTION_FACTORY_UNSUPPORTED`: it cannot infer true owned aggregate
layout or emit collection ABI from its current i64/bool-only SSA nodes.

## Not implemented

**Map.of / Entry.of / Pair.of / Array.of / List.of / Vector.of / Set.of /
Record.of, mutable arrays, real heterogenous tuple storage, tuple variables,
non-literal tuple members, guest collection ownership and actor transport are
not implemented on .NET.** All require a real typed aggregate and ownership
representation in HIR, verified CFG, CIL and full NativeAOT execution.

The parser also rejects `~map`, `~array` and other unsupported
tilde constructors; no host `Dictionary`, `List`, `ValueTuple` or C#
reference model is substituted to claim native AttoLang guest support.

## Correctness and hardening

- No `Tuple` namespace collision: shipping scalar frontend accepts only one
  top-level `main` with i64 expression; nominal class/member declarations
  are unsupported. Only the exact `Tuple.of` compiler-owned spelling admits
  scalarized projection, not `obj.of`/imported class member.
- Source lexer does not reinterpret strings/comments as guest constructors.
- Up to 32 elements / type witnesses, UTF-8 source and AST depth budgets.
- Negative tests: arity, out-of-range, negative/nonconstant index, malformed
  types, unsupported values, escape, other factories, no silent CIL fallback.
- Authored test performs `OresParser.Parse` → `OresIrLowering.Lower` →
  `CilEmitter.EmitLibrary` → inspection and independent CIL evaluator.
  **Authored does not mean executed**.

## Next real implementation

1. Establish a nominal/structural `AggregateType` and ownership facts at
   source TypeChecker, not a synthetic `long` for a Map or Array.
2. Add verified CFG instructions for tuple packing/projection, owned array
   allocation and checked reads/writes, each with explicit lifetime and quota.
3. Emit deterministic NativeAOT-safe IL without Reflection.Emit,
   `Dictionary<object,object>` or ambient heap identity fallback.
4. Implement immutable `Entry.of`, type-approved `Pair.of` aliasing, then
   `Array.of` / `List.of` / `Vector.of`; `Map.of` and `Set.of` require
   stable key equality, duplicate detection, transactional rollback and
   move-only ownership. Preserve established `Tuple[A,B]`, `Array[T]`,
   `Vector[T]` and fixed/repeating collection shapes.
5. Run exact-head .NET tests and publish NativeAOT compiled guest execution
   with actual nonzero-step runner evidence. Separate host-only checks from
   compiler-produced artifacts. Only then advance conformance issue #434.

Related language contracts: attolang-language-spec.md #27,
oreslang-common-source #257 and oreslang-platform-conformance #641.
