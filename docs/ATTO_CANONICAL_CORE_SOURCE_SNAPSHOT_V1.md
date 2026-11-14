# AttoLang canonical numeric/core snapshot — .NET-NativeAOT

This backend stages **AttoLang source bytes only**, not compiler, native guest, or AOT evidence. No production import routing is enabled by adding these fixtures.

| Canonical module | Git blob (SHA-1) | Backend status |
|---|---|---|
| ores.core/numeric (numeric.atto) | 5ced991f1d49b3c2a60fdd72b9003c0a4d18cd72 | SOURCE_ONLY |
| ores.core/collections (collections.atto) | c5d6659dda078736cb170d5c1d59fc04e700d023 | SOURCE_ONLY: 9 typed map/filter/reduce variants; NativeAOT guest execution blocked |
| ores.std/core (core.atto) | d2cd9b8e0cd3e3bb3ee6a5f9cebf9ab3312c44f4 | SOURCE_ONLY |

Origin: Java/GraalVM canonical migration PR #2542, exact head a81eb643ab4ed194d9641b3c87ac2b8c59b0b4b2. The blobs match the canonical AttoLang implementations, with no host Java algorithms added.

**Admission:** python3 -I tests/check_atto_core_snapshot.py; python3 -I -m unittest discover -s tests -p 'test_atto_core_snapshot.py' -v. The gate rejects corruption, source-size overflow, symlinks, BOM, NUL, malformed UTF-8, extra files and legacy duplicate sources. All outcomes are SOURCE_ONLY.

**Future execution gate:** separately implement tier-aware backend compiler-owned source registration, a strict canonical .atto/legacy .ores alias identity, real compilation and native host/guest test execution. A zero-step GitHub job is NOT_EXECUTED, not a compiler failure. Cross-organization private source transfer remains independently approval-gated and is not used here.

## Native collection combinator parity gate

The source snapshot contains `Array`, `Vector`, and `List` implementations of
`map`, `filter`, and `reduce`, byte-identical to the shared Atto core branch.
The production .NET compiler still rejects unverified collection access and
cannot lower generic, first-class callback-based collection combinators.
The source snapshot verifier establishes **exact bytes only**, never working
collection imports or NativeAOT guest execution. The release gate requires:

1. Checked actor-owned array/vector/list storage with 32-bit offsets and
   bounds, generation, capacity, ownership and allocation-failure handling.
2. Typed generic specialization, callable signatures and closure captures;
   callback effect and suspension rejection; no CLR reflection or dynamic JIT.
3. Correct output container identity, deterministic order, explicit empty
   `reduce` initializer, and exactly once callback evaluation per element.
4. Negative tests (type/arity mismatch, moving non-Copy inputs, cross-actor
   borrow, escaping mutable loan and bad index), followed by .NET NativeAOT
   native **guest** execution of the same `.atto` fixtures as Java and LLVM.

Until then report **SOURCE_ONLY / NOT_PARITY_VERIFIED**.
