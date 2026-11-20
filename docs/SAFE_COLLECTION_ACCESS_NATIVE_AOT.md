# AttoLang safe array and collection access — .NET admission gate v1

**Scope:** `oreslang-source.net`, shipping scalar and experimental structured
frontends. This change is **fail-closed**, not native guest collection support.
`oresnet check`, `compile`, `check-structured`, and `compile-structured`
must refuse any unverified `[` / `]` executable syntax with
`E_SAFE_COLLECTION_ACCESS_UNSUPPORTED`. Trivia and quoted module import paths
must remain inert. Existing signed-i64 source and verified CIL must continue to work.

## Target AttoLang semantics (not yet implemented here)

- `Array<T>.get(index)` and `Vector<T>.get(index)` return `Option<T>`
  (or an explicit read loan for non-Copy values). Out of range returns `None`;
  negative, overflowed or forged ref32 offsets never become native pointers.
  `Some(None)` and `None` are distinct; no implicit flattening/conversion.
- Direct `xs[index]` syntax is forbidden until there is explicit checked
  lowering with a defined absence/error result. Never implement this as a CLR
  `IndexOutOfRangeException` exposed to guest source or unchecked host load.
- `rt borrow` permits multiple immutable readers, `rt mut borrow` grants
  one exclusive writer, and `rt take` invalidates the source. Require a
  compiler-checked loan lifetime, move/alias exclusivity, use-after-move tests,
  and forbidding outliving the owner. An alias may not cross an actor mailbox,
  suspension, actor teardown, or migration without separately proven ownership
  and generation fencing. No host-only shadow borrow check counts as parity.
- `get_mut` must return a scoped exclusive loan, not an unguarded reference.
  Reallocation/resizing is forbidden while any element loan is live. A mutable
  collection snapshot must be versioned and owned: cross-thread access is
  immutable snapshot publication, or a short non-suspending `critical` region
  for copy-on-write root swap. `critical` does not make an unsafe alias safe.
- The actor's 32-bit **unsigned actor-relative** guest offset must be checked
  with widened arithmetic against arena capacity, allocation length,
  alignment, object generation, owner and tenant before access. Native
  addresses may remain 64-bit but cannot be stored as guest ref32 values.
- Map lookup returns `Option<V>`; failed remove/insert/resize must not
  partially change quotas, pointers, generation or borrow states. No implicit
  nested `Option` flattening, and no functions/closures over channels.
- Portable generic `Option`, collection APIs and algorithms belong in
  canonical `.atto` core/std code. C# is only for verified compiler, CIL
  emission, bounded actor memory/ABI primitives and native interop.

## Required real compiler acceptance (future PR)

1. Accept a legal `.atto` array declaration, construct and `get` with
   exact typed `Option` discriminant/payload and verified CIL/NativeAOT result.
   Verify out-of-range, zero length, max ref32 offset, nested Option and
   value ownership in the emitted program, not just host-model tests.
2. Compile negative alias conflicts, use-after-move, mutation during active
   read loan, resize while borrowed, escape beyond owner, and read across
   suspension. Require expected **semantic** diagnostics, not generic parse
   failures.
3. Prove exactly-once index evaluation, immutable read consistency, atomic
   quota/generation rollback on allocation denial, and actor A isolation.
4. Run at the exact compiler head on Linux and Windows .NET 10 Native AOT
   with non-zero executed steps, binaries/artifact digests and independent
   review. Zero-step CI is `NOT_EXECUTED`; this v1 gate is not
   borrow-checker/Option/guest-collection parity.

**Naming:** public language is AttoLang; preserve `oresnet`,
`Ores.Net.*`, Oreslang repository names and legacy `.ores` inputs.
