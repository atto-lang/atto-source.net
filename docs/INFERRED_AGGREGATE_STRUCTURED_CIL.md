# AttoLang inferred scalar aggregates — .NET structured AOT slice

**Status:** source implementation staged for verification; not a production
Native AOT aggregate runtime, not shipped bootstrap compiler parity.

The **experimental** check-structured / compile-structured frontend can
now admit immutable closed scalar aggregates inside a function, including
runtime-valued `val` aggregates with checked source-ordered SSA field snapshots.
The existing oresnet check / oresnet compile scalar parser remains explicitly
fail-closed for inline aggregate constructors.

Supported source:
- const x = infer struct{a: 20}, infer record{a: 20}, and record{a:20}
- val x = infer struct{a: runtime_int}, val x = record{a: runtime_int}
- const y = infer struct{...x, b: 22}, infer record{...x, a: 42}
- int/bool constant expressions in named fields, and projection x.a
- rightmost-wins inferred field types with exact struct vs readonly record kind
- duplicate named fields, unknown spread sources, invalid projections and
  aggregate escape are rejected

Implementation: ClosedAggregateSpreadPlan constructs a static checked row
and ordered schedule; the structured frontend folds scalar values into
typed SSA literals for compile-time `const` rows. Runtime `val` rows
emit private ordered SSA field snapshots, including overridden writes.
CIL emission uses pre-existing verified scalar CFG
without heap objects, runtime reflection, Box, Arc, dyn or tracing GC.

**Not implemented:** non-Copy fields,
nominal/explicit struct constructors, aggregate parameter/return ABI,
mutability, actor transport or general structural readonly views.
Any such source must fail before CIL emission.

Required before promotion:
1. Run StructuredCfgLoweringTests and ClosedAggregateSpreadPlanTests
   at exact head, including CIL interpreter and deterministic PE comparison.
2. Execute compile-structured -> Linux and Windows Native AOT with
   examples/inferred-aggregate42.atto; require OS exit 42.
3. Compare identical source fixtures against Java, LLVM and Wasm.
4. Preserve production bootstrap's negative admission tests.
5. CI checks with no executed steps do not establish parity.
