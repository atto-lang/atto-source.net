# Signed i64 relational operations — experimental .NET CIL parity

This narrow compiler change adds `>`, `<=`, and `>=` to the existing
structured-source signed `int` comparisons alongside `<`. Both operands
must be typed i64; each is lowered exactly once, left-to-right, into verified
typed CFG. Signed `clt`/`cgt` CIL implements the comparisons, with an explicit
boolean inversion for `<=` and `>=`. Const expressions use matching signed
64-bit semantics and preserve checked arithmetic in the operands.

The canonical new regression fixture is
`examples/structured-signed-comparisons.atto`.
`StructuredCfgLoweringTests` compares the independently verified CFG evaluator
with a test-only interpreter of emitted deterministic PE/CIL. Cases include
negative operands, signed extrema, equality boundaries, invalid bool/int
mixing, malformed operators, and reachable operand overflow.

**Scope:** experimental `check-structured`/`compile-structured` only.
The shipping scalar `check`/`compile` grammar is not broadened. This
change does not add floating-point comparisons, actor semantics, dynamic
dispatch, class/enum support, or general source parity with Java/LLVM.

**Verification:** run
`dotnet run --project verification/Ores.Net.Tests -c Release`, then execute `bash scripts/publish-linux.sh
examples/structured-signed-comparisons.atto artifacts/relational --structured`
and verify stdout 42. The Windows workflow uses `compile-structured`, a
statically linked Native Host publish and actual PE execution; the generic
Windows script remains scalar-only. The CIL test interpreter is NOT real
CLR/Native AOT execution; runner-free or zero-step CI is
NOT_EXECUTED for guest parity. Cross-backend execution on Java and LLVM
requires independent exact-head binary/fixture receipts.
