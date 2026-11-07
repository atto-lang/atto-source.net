# NativeAOT compiler-owned canonical Core numeric v2

This extension embeds the existing **unchanged** `numeric.atto` file into
the .NET compiler assembly and resolves six `atto.core/numeric`/
`ores.core/numeric` alias spellings to one canonical unit
`@atto.core/numeric.atto`. A user project file is never consulted for a
compiler-owned import.

The structured parser now admits the canonical `if CONDITION; do ...
fi` syntax directly into typed branch AST/CFG, with nested branch
scopes, early returns and definite-return analysis. Signed i64 greater
than is a new typed operator through CFG verification, CIL `cgt`
emission, and the independent reference evaluator. Existing brace
if/else and stage-1 identity syntax remain intact.

This is authored compiler implementation, **not** verified NativeAOT
execution. The currently blocked CI must compile the canonical numeric
fixtures, verify source/graph/compiler/artifact digests, run Linux and
Windows native guests, and reject shadowed or forged imports before
claiming parity.

## Real executable CIL numeric differential regression group

The module-linking self-test now emits actual typed CIL and runs its
verification interpreter for 12 canonical numerical operations:
min/max (including equality), clamp low/high/midpoint, reversed
bounds, equal bounds and extreme signed values. Six alias probes
check the same embedded numeric source witness, and an ambient
project shadow returning 99 must not change the CIL result 42.
Seven negative namespace/path spellings, an invalid module alias
and an unclosed do/fi scope must fail. The checked CIL verifier
test interpreter now understands the signed `cgt` opcode, matching
the compiler and independent CFG reference evaluator.

These authored tests are NOT hosted execution results; the native
Linux/Windows runner still needs to allocate and run them.
