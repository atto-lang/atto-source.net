# Hosted HTTP/FS pure-policy compiler gate — direct CIL stage

**Evidence: not executed on current head (source-org runner did not admit jobs).**
This is a source-compiler change, NOT a running HTTP client or filesystem.

The canonical shared native Atto source is embedded **only as inputs to the
compiler gate**, not substituted with C# implementations:

- `stdlib/std/http_response_policy.atto` Git blob `22757dafe89bc9601d04c8726a09ddf676206c7e`
- `stdlib/std/fs_text_policy.atto` Git blob `0cd53216e91ef8de1a764ba5b80ce85c5796c1f2`
- `fixtures/cil/hosted-policy-main.atto`: identical source with the native LLVM AOT branch

The direct CIL stage now parses and type-checks simple bool-returning functions,
integer comparisons `< <= > >= == !=`, bool constants, and
`if condition then return value; fi` guards preceding the final return.
The real `System.Reflection.Metadata` emitter creates CIL boolean signatures,
integer-checked math and forward branches. **Guard order must short-circuit**:
the signed range/size functions avoid overflow by returning before subtracting
untrusted ranges. No implicit truthiness or cross-type equality is admitted.

## Executable acceptance

From the repo root on an authorized build runner with .NET SDK 8 and NativeAOT:

```bash
bash scripts/test-cil-guest.sh
```

The script first runs legacy scalar guest and negative tests, then SHA-pins
the exact Atto policy bytes, forms a bounded single-file compilation input
from the two native Atto modules and the shared guest entry fixture, and
compiles it using `Atto.Cil.Compiler` into real ECMA-335 CIL.
The CLR host must exit **42**, then a **fresh** self-contained NativeAOT
publication against that generated guest assembly must also exit **42**.
The guard test covers HTTP status/body decisions, FS byte limits, negative
counts and signed-i64 boundary cases including guards that prevent checked
arithmetic overflow. Additional negatives require `E_TYPE`, `E_SYMBOL`,
`E_ENTRY`, nonzero compiler exit and **no output DLL**.

**Scope restrictions:** source files are concatenated only by this test
harness; the compiler does not implement real cross-file `pkg "std.http"`
linking, `std.http.get`, `std.fs.read_text/write_text`, async `susp`/`await`
or actor/tenant authority. CIL/NativeAOT machine code is guest execution only
when an actual executed build produces matching output. A successful C# host
reference model is not sufficient.

**Overlap:** direct CIL match/stdout work [PR #26](https://github.com/atto-lang/atto-source.net/pull/26)
also changes `compiler/Atto.Cil.Compiler/Program.cs`. It has a different
independently tested guarded match/stdio feature; do not merge or discard
either branch without semantic reconciliation and fresh exact-head tests.

**CI:** `.github/workflows/atto-direct-cil-guest.yml` triggers the script for
compiler, fixture and script changes. Zero-step Actions runs are
`NOT_EXECUTED`; they do not mean parser, IL or NativeAOT tests failed.
