# AttoLang nominal enum internal helper v2 — .NET NativeAOT

This opt-in `check-enum-v1` / `compile-enum-v1` implementation adds a
single statically named `fnc next(Light state): Light` helper with exhaustive
nominal match and returns to the restricted v1 nullary subset.

The source compiler validates exact enum *owner identity* for the helper's
parameter and return, every matched variant and return case, and the
compiler-created argument in `main`. Wrong owners, missing arms, numeric
forgeries, duplicate cases, guarded arms and extra declarations fail closed.
This *does not* provide a general type descriptor in the shared CFG IR:
after source checking the private static helper and its caller are lowered to
bounded i64 slots via the independently verified CFG/CIL linker. No guest can
pass an arbitrary i64, import an enum, or expose the helper as a public enum
ABI in this lane. Generic payload, borrowed/owned cases and actor mail remain
unimplemented.

Canonical fixture: `tests/fixtures/adt-nominal-function-v2.atto`,
byte-for-byte identical to the LLVM backend. The verification executable
compares emitted static MethodDefs, checks a real `call helper_next`
instruction, runs the bounded CIL interpreter on all three input cases, and
asserts 16 adversarial semantic-negative cases.

Real guest CI uses the private `gha-indie-worker/gha-indie-worker-docs`
carrier with source Git blob identities verified against the source branch.
A generated `OresGenerated.dll` is referenced and compiled into a Linux
x64 NativeAOT guest executable for Red, Green and Yellow inputs. The
expected `main` results are 0, 30 and 10 respectively.

This stage does NOT certify general nominal IR types, imported owner identity,
actor arenas/borrow/move, zero-copy symbols, full original Git-tree mirror, or
the cross-backend canonical 12-case ADT matrix. Independent review and
compiler-backed full-source integration remain required before merge.
