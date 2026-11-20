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
An equivalent closed readonly-record slice admits
`Record.of(low: 11, high: 23).high` and
`~record(low: 11, high: 23).high`; duplicate fields, unknown field
names and non-literal values fail before CIL. The record projection is
also statically scalarized and cannot escape as an object.
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

**Map.of / Entry.of / Pair.of / Array.of / List.of / Vector.of / Set.of,
general Record.of values, mutable arrays, real heterogenous tuple storage, tuple variables,
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
  types, duplicate/unknown record fields, unsupported values, escape,
  other factories, no silent CIL fallback.
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


## 2026-11-10 native actor aggregate ABI milestone

Native backing now lives in `runtime/Ores.Net.Runtime/ActorNativeAggregateStore.cs`:

- `Tuple`, `Record`, `Entry`, `Pair`, and fixed `Array` have separate
  native tags and fixed 24-byte headers plus 8-byte packed signed-i64 elements.
- Every aggregate is a generational `ActorNativeArena` allocation, requiring a
  distinct *store instance per actor*. A handle from a neighboring actor or
  a dropped/reused generation cannot read or mutate this store's bytes.
- Products are immutable; `Array` permits checked indexed writes.
  All allocation failures return the invalid handle, and failed initialization
  scrubs/releases the native allocation before returning control.
- `Record` needs a positive schema ID and static ordered field layout.
  This backing API **does not itself authenticate a compiler-minted schema ID
  or tenant**, and must not be exposed directly to untrusted guest/FFI callers.

A separate `compiler/Ores.Net.Compiler.Core/NativeAggregateLayoutVerifier.cs`
provides immutable compiler-side i64-only layouts, bounded product/array
sizes, explicit empty-array element witnesses, duplicate/static record field
checks, and fail-closed List/Vector/Map/Set admission. Its numeric kind tags
are tested against the runtime's unmanaged ABI. No Bool/pointer graph is
silently lowered into native signed-i64 storage.

**Crucial evidence boundary:** the native store and verified layout are not
yet wired into the shipping scalar OresIR/CFG/CIL emitter. There is no
source-level escaping `Tuple<T...>` or `Array<T>` guest value yet. The
existing `Tuple.of(...)[constant]` and `Record.of(...).field` are still
closed scalarizations. Native runtime/HIR tests do not establish source guest
implementation parity.

Authored verification:
- `ActorNativeAggregateStoreTests`: type/nominal disjointness, actor isolation,
  generation fencing, immutable product guard, mutable Array bounds, 48-byte
  quota rollback and exact reclamation.
- `NativeAggregateLayoutVerifierTests`: type/layout/budget/record-field
  diagnostics, frozen immutable descriptor snapshots, compiler-runtime tag
  equivalence, unsupported kinds.
- `verification/Ores.Net.ActorMemoryProbe/Program.cs` runs a separate
  **NativeAOT host-runtime** actor aggregate security probe. The source-owned
  `actor-memory-native-aot.yml` requires all three success markers.
  The probe is explicitly labeled `NOT compiled AttoLang guest`.
- The positive/negative `.atto` guests under
  `tests/fixtures/collection-factories/` still use the shipping scalar
  compiler path and are separately checked by
  `atto-collection-factory-nativeaot.yml`.

CI admission on source org still fails before step allocation. The
`.github/private-ci-approved-repositories.txt` and
`.github/private-ci-approved-source-snapshots.txt` policies have no active
approved destination/snapshot for this head. No private source was exported.

## Smallest compiler follow-up

Add verified product/array typed HIR facts to source AST, then explicit CFG
aggregate-allocate/read/write/drop instructions and an independent verifier.
The System.Reflection.Metadata emitter must statically reference the
actor-native storage ABI, enforce checked nominal shape and per-actor
capability, and deterministically clean up on normal/throw/panic paths.
Do not silently return a scalarized constant for an escaping aggregate.

After this is compiled and executed at an exact head, wire Entry/Pair product
factories and then List/Vector growth, Map/Set stable-key and rollback
semantics. Preserve separate nominal types and the legacy tuple/array/vector
type/shape syntax.
