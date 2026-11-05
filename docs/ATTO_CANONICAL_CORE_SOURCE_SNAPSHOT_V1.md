# AttoLang canonical core source snapshot — .NET NativeAOT

This repository contains an independently reviewable **source-only candidate**, not a native execution certification. The two physical .atto files are byte-identical to the compiler-owned Java/GraalVM PR #2542 sources at head `a81eb643ab4ed194d9641b3c87ac2b8c59b0b4b2`.

| Module | Compiler-owned import | Git blob |
| --- | --- | --- |
| numeric.atto | ores.core/numeric | 5ced991f1d49b3c2a60fdd72b9003c0a4d18cd72 |
| core.atto | ores.std/core | d2cd9b8e0cd3e3bb3ee6a5f9cebf9ab3312c44f4 |

The .NET NativeAOT compiler has **not** been wired to load this snapshot; adding the files does not change guest runtime behavior or import admission. A future separately reviewed compiler integration must preserve freestanding vs hosted tiers, explicit .ores compatibility aliases, stable logical unit identity and guest conformance, with both negative admission and real executed .NET NativeAOT receipts.

Run `python3 -I tests/check_atto_core_snapshot.py` and `python3 -I -m unittest discover -s tests -p 'test_atto_core_snapshot.py' -v`. The gate refuses altered blobs, oversized/non-UTF8 sources, symlinks, extra files, and physical legacy duplicates. Its output is explicitly `compiler=NOT_EXECUTED`. No cross-org CI mirror or private source transfer was used. Origin source and destinations all belong to the same existing organization; this is not an alternate Actions carrier.
