# .NET nominal enum v1: executable nullary closed-match slice

This is a **new opt-in**, deliberately restricted `.atto` source-to-CIL
compiler path. It neither alters the shipping `OresParser` admission rules
nor makes `check-structured` accept generic ADT source.

```sh
oresnet check-enum-v1 tests/fixtures/adt-nominal-nullary-v1.atto
oresnet compile-enum-v1 tests/fixtures/adt-nominal-nullary-v1.atto --out path/OresGenerated.dll
```

The admitted program has exactly one locally defined enum with 2–32
**nullary** cases and a public parameterless `main(): int`. A `val
EnumType local = EnumType.Case` declaration binds the sole enum value; one
complete `match local over on EnumType.Case -> { return N; } ... end`
returns a checked signed-i32 literal from every branch. Constructor
parentheses may be empty. Cases are compiler-checked by *nominal owner
and variant*, never by payload shape or guest-visible raw integers.

The compiler rejects duplicate case names/tags, zero/out-of-range tags,
foreign owner constructors/patterns, missing/duplicate patterns,
non-nullary payloads, raw integers masquerading as symbols, and
unsupported source forms. It then maps safe **compiler-private** tags to
verified `StructuredProgram` CFG equality branches, emits deterministic
static CIL using the existing metadata emitter, and allows compilation
into a Linux x64 NativeAOT guest executable (when linked at build time).

This slice has no external enum parameters, imports, actor messages,
borrowed/owned payloads, reflection, dynamic codegen or generic enum
layout. It is not the 12-case nominal ADT parity suite; use that
independent suite to govern later improvements. No numeric case tag is
exposed through guest source or a public enum ABI. Every reachable value
comes from the compiler-validated local constructor.

**Evidence classes:** the reference evaluator is host-only, Mono.Cecil
CIL interpretation is emitted-code verification, and a published
NativeAOT guest *calling the generated `OresProgram.Run()`* is executable
guest evidence for the narrow nullary subset only. None proves payload
ownership, actor wire safety, exhaustive finite product matching, or
cross-backend nominal parity. Require exact source blob identity and
nonzero executed runner steps before recording a successful native run.
