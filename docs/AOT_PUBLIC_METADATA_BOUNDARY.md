# Closed AOT public metadata reference — task #34

`AotPublicTypeCatalog` is an immutable, explicitly constructed, sorted
public descriptor table. It does not query `System.Type`, CLR reflection,
runtime objects, actor state, unmanaged arena pointers or dynamic assemblies.
It denies unknown type IDs and late generic type names; it exposes only
compiler-supplied approved public field kinds and method arities.

**NOT IMPLEMENTED:** `type_of<T>()` source parsing, typed generic-closure
proof, CIL metadata generation, trim-safe linker rooting or dead descriptor
elimination, AOT publish/execute of a guest program, public
`fields_of`/`methods_of`/`implements` source calls, runtime-issued
actor inspection tokens or independent binary receipts.

The included smoke test is host-only. It cannot prove an AttoLang
guest reached this table, or that .NET trimming discarded unrelated
descriptors. A release requires the NativeAOT Linux/Windows two-target
guest proof, negative source compilation (no artifact), signed/reviewed
exact-head CI, actual trim graph inspection and independent approval.

Kept in a separate branch; do not mutate ongoing actor/CIL implementation
PRs. Tracking: issue #342 and shared conformance PR #318.
