# Compiler-owned AttoLang libraries (.NET)

Layered `core`/`alloc`/`std` units per
[atto-spec core-alloc-std-libraries-v1](https://github.com/atto-lang/atto-spec/blob/main/language/core-alloc-std-libraries-v1.md),
embedded in `Ores.Net.Compiler.Core` and imported with
`import module num from "atto.core/num";` (`atto.core/` and `.atto` are aliases).

| Unit | .NET status |
| ---- | ----------- |
| `core/num` | int/bool slice: all functions except `checked_add`/`checked_sub` |
| `core/cmp` | complete |
| `core/mem` | `is_power_of_two`, `log2_floor` (no `align_up`/`align_down`/`is_aligned` yet) |
| `core/option`, `core/result`, `alloc/arena`, `alloc/vec`, `std/io`, `std/path` | not implemented: fail closed with "not implemented by the .NET backend" |

The module linker currently lowers the checked int/bool function ABI only;
units that need `Option<int>`, `Result<int,int>`, strings or host output wait
for typed module lowering. `verification/Ores.Net.Tests/AttoLibrariesV1SelfTest.cs`
links, emits and executes the shared `tests/atto_libraries` corpus and reports
unimplemented units as UNSUPPORTED, never as passes.

`atto.core/identity` and `atto.core/numeric` (SHA-pinned) remain as before.
