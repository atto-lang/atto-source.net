# Direct-CIL integer division: fail-closed floating-result admission

**Status:** candidate, not certified. This change does not implement floating-point code generation.

## Canonical semantic decision

The accepted owner decision in [atto-spec PR #151](https://github.com/atto-lang/atto-spec/pull/151) is that `int / int` returns a floating-point value, not an integer. A separately specified `Integer.div_trunc` operation is required for truncating division. The existing signed-i64-only `Atto.Cil.Compiler` accepts functions returning `int`, and previously emitted `ILOpCode.Div` directly for source `/`. On inputs `5 / 2`, that would produce `2`, contradicting the canonical result `2.5`.

This draft adds `E_DIVISION_FLOAT_RESULT` to the source semantic walk and an independent backstop in the CIL emitter. Any `/` in this restricted integer-only compilation profile fails before the destination DLL is created. This must **not** be represented as executable division parity. Other checked integer arithmetic, direct helper calls, and existing good-guest execution stay enabled. As a separate checked-i64 parser hardening, this branch now accepts the exact signed minimum literal `-9223372036854775808` (including as an argument to a helper), without accepting the out-of-range magnitude `-9223372036854775809`. The minimum is lowered as a single i64 constant rather than as `0 - 9223372036854775808`, which would fail during parsing.

## Real source/guest validation gate

The existing `scripts/test-cil-guest.sh` suite now includes three additional `.atto` input programs: direct `5 / 2`, a division inside a called helper, and a nested division inside addition. Every negative must exit status 2 with `E_DIVISION_FLOAT_RESULT`, and produce no artifact. Existing positive `.atto` programs must still generate real ECMA-335 CIL and execute through CoreCLR and NativeAOT with the same exit values. An additional signed-minimum arithmetic fixture must produce exit code 42 through both CoreCLR and a separately published Linux NativeAOT binary; the one-below-minimum negative must report `E_LITERAL` and emit no DLL. The full restricted-compiler matrix now contains four positive fixtures and sixteen negatives. The dedicated workflow checks the exact PR head and matching Git blobs before running the suite.

**Execution state:** not independently run at this head. A source-only inspection or `steps=[]` CI attempt is NOT_EXECUTED, not a compiler or guest PASS. The target compiler still lacks typed `float` signature/return, mixed int-to-float conversions, compiled float arithmetic, `Integer.div_trunc` and division-error taxonomy. The signed-minimum addition is a parser/guest arithmetic regression, not floating division support. Add those in a separate verified compiler feature only after the spec contract and shared conformance cases are stable.

## Overlap disposition

- [atto-spec #151](https://github.com/atto-lang/atto-spec/pull/151) already documents the owner-approved division rule; do not create another conflicting spec policy.
- [atto-source.java #66](https://github.com/atto-lang/atto-source.java/pull/66) is the existing checked-i64 implementation; it is independent and does not certify floating division.
- [atto-source.net #120](https://github.com/atto-lang/atto-source.net/pull/120) is a source-only checked arithmetic oracle; it does not add compiled floating-result division.
- This change covers only `compiler/Atto.Cil.Compiler`, **not** the separate legacy Ores.Net compiler/structured path or other CLR guest emitters. Those require separate inventory and parity gates.

No merges, policy overrides, private-source CI mirroring or secrets are authorized by this draft.
