# Shared checked-i64 parity corpus — .NET verification lane

**Status: source-only reference and tests, not .NET compiler or NativeAOT execution.**

The copied `cases.tsv` is byte-identical to the 15 checked-i64 cases in [Atto Java #66](https://github.com/atto-lang/atto-source.java/pull/66), [Atto JS #69](https://github.com/atto-lang/atto-source.js/pull/69), and [central all-eleven conformance #235](https://github.com/atto-lang/atto-platform-conformance/pull/235):
- Exact Git blob SHA-1: `71309fac2ab6b2191a0056c84d78987bd342dce0`
- Raw SHA-256: `e56dfccea11757ff18fc72a81ba5d643380e69184ab309eedf45e1e7b940e133`
- Cases: seven signed-i64 values, seven arithmetic overflow scenarios (including canceled intermediate overflow and negated argument), one zero divisor.

`tests/test_shared_checked_i64_corpus.py` executes a bounded Python AST reference, not arbitrary Python evaluation or C# compilation. It accepts only the five named arithmetic functions with exact arities, checks every intermediate for signed-64 range, treats the minimum signed literal correctly, and implements truncating signed remainder (`-7 rem 3 = -1`, unlike Python's native `%`). It rejects unrecognized calls, nonintegers, booleans, invalid arities, malformed cases, changed expected outcomes and tampered source content. It independently recomputes both corpus hashes.

Run the exact Git-borne source-only test:
```sh
python3 -m unittest discover -s tests -p test_shared_checked_i64_corpus.py -v
python3 -m py_compile tests/test_shared_checked_i64_corpus.py
```

The checked-in source-local workflow `.github/workflows/shared-checked-i64-oracle-only.yml` pins the triggering Git head and Git blobs before running these tests; it does not copy private source across organizations, create release artifacts or claim CIL/NativeAOT guest parity.

**Release blockers**: the authoritative checked-default policy remains proposed in [atto-spec #144](https://github.com/atto-lang/atto-spec/pull/144). The .NET backend's [numeric implementation issues #73](https://github.com/atto-lang/atto-source.net/issues/73) and [#74](https://github.com/atto-lang/atto-source.net/issues/74) remain the real CIL/NativeAOT implementation track. To mark `.net` as passed in an eventual authenticated receipt, compile **real parametrized .atto guest** expressions to CIL, publish actual Linux and Windows NativeAOT executables, run all 15 positive/negative cases, verify diagnostic vs runtime fault provenance, and retain binary SHA-256 and exact HEAD/tree/step receipts. A Python oracle test cannot substitute for any of these requirements.
