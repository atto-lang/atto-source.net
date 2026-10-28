# Exact canonical Oreslang numeric source through .NET CIL (draft)

This draft targets `main` after .NET source module linker PR #229 merged. Its
`library/ores.core/numeric.ores` is byte-identical to the source authored in
`oreslang-common-source` PR #179 at commit
`736a11faa62277696384c770ddf0008a3bf2a039`.

- SHA-256: `3d1d7b77068dd02b3d60b30b72f29a4537a933134ca79e1e81df096dc56f5f33`
- Git blob SHA-1: `5ced991f1d49b3c2a60fdd72b9003c0a4d18cd72`
- API: `min_int`, `max_int`, `clamp_int` (signed int64).

This is an identical *vendored source snapshot*, not a C# stdlib
implementation. The experimental linked-unit parser is extended to accept
native Oreslang `if ...; do ... fi`, along with signed `>` lowered to
typed CFG and signed CIL `cgt`. The closed compile-time registry maps
`ores.core/numeric` to the embedded, digest-checked source resource
rather than looking through project paths or loading another module. The shipping scalar compiler remains unchanged.

`examples/modules/core-numeric.ores` imports unchanged native library
source by its canonical name without a dependency root. C# unit regressions
check the digest, root-independent graph identity and byte-identical CIL across
relocated projects, including signed
greater-than, shadow resistance, unknown/duplicate canonical imports,
int64 minimum and values beyond the IEEE 754 exact integer range. New source regressions
also require signed comparison constant folding and false-branch CFG fallthrough
to agree with canonical compiled numeric calls. The CI workflow requires the native-compiled compiler to compile
the original .ores files, then Native AOT-publishes the statically linked
application on Windows and Linux and requires exit 0 / output 42.

**Limitations:** this is only a closed compiler-owned **single-module** v1
registry, not a general shared package manager, compiler-controlled lockfile
or an implementation of other standard library modules. Tests have not been
executed against this candidate head by this session; workflow and tests are
admission gates, not successful receipts. Review and exact-head execution are
required before promotion and merge. No external-org private tree transfer
is authorized by this work.
