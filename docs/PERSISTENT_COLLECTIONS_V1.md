# Persistent immutable collections v1 (.NET runtime)

Status: **draft, first executable runtime slice**. Feature 10 of the 15
proposals.

Related: atto-spec #195 (const `Vector` immutability and persistent
functional updates) is the normative surface. atto-source.rs #491/#493
compile *statically known scalar* `Vector.appended/updated` to Wasm. This PR
adds the general **runtime representation** a backend needs once contents
are dynamic.

## What ships

`runtime/Ores.Net.Runtime/Persistent/`

| Type | Structure | Operations |
| --- | --- | --- |
| `PersistentMap<K,V>` | HAMT, 32-way bitmap nodes plus collision nodes | `SetItem`, `Remove`, `TryGetValue`, `ContainsKey`, enumeration, `ContentEquals`. O(log32 n) path copy. |
| `PersistentVector<T>` | 32-way radix trie plus 32-element tail, `T[]` leaves (no boxing) | `Add`, `SetItem`, `RemoveLast`, indexer, enumeration, `From`. O(log32 n) path copy; append amortised O(1) through the tail. |

Properties:

* **Snapshots are O(1):** every update returns a new value sharing all
  untouched nodes, and old versions never change. This is the property that
  lets an actor publish state to readers without copying or locking.
* **No-op updates return the same instance** (`SetItem` with an equal value,
  `Remove` of a missing key), so change detection is a reference check.
* **Canonical removal:** lone entries bubble up, so delete-heavy workloads do
  not leave single-child chains.
* **Hostile hashes degrade, never corrupt:** a full collision falls back to
  a linear collision node.
* No reflection, locks or finalizers. The test host is `IsAotCompatible`.
* Map iteration order is **unspecified** (hash order). AttoLang must not
  expose it as stable; a sorted map is a separate future type.

## Tests (`tests/PersistentCollections.Host`)

Deterministic model checking against `Dictionary`/`List`:

* map: 30k int ops, 20k string ops, 4k ops with only 4 hash codes (collision
  nodes), and 6k ops with hashes equal in the low 25 bits (deep splits).
  Count and lookups are checked after every op, with ~60 retained snapshots
  re-verified at the end.
* vector: 40k appends (crossing 32, 1056 and 32800, so three trie levels),
  20k random `SetItem`, drain to empty with checks at every level boundary,
  16 snapshots re-verified, regrow, and the bounds check.

## AttoLang mapping (proposed)

```atto
val Vector<int> a = vec[1, 2, 3];
val Vector<int> b = a.appended(4);      // a unchanged
val Map<String, int> m = map{"x": 1};
val Map<String, int> n = m.with("y", 2).without("x");
```

`const`/`val` collections lower to these types on .NET. Because they are deeply
immutable, they are `Sendable` across actors by reference (zero copy). That
removes the need to serialize-copy immutable payloads at mailbox boundaries.

## Next slices

1. Typed CIL lowering for `Vector<T>`/`Map<K,V>` in the structured backend.
2. Transient (owned, single-actor) builders for O(1) amortised batch
   construction, invalidated on freeze; enforced by the ownership checker.
3. `PersistentSortedMap` (balanced tree) for ordered iteration.
4. Shared conformance corpus (atto-platform-conformance) with rs/Java.
