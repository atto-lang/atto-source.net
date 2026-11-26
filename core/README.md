# Core slice, iter and cmp — .NET Native AOT compiler tier

Compiler-owned, SHA-256-pinned `atto.core/{slice,iter,cmp}` resource identities
resolve from `library/atto.core/*.atto`; each file matches the backend-owned
`core/*.atto` source. Exact `ores.core/*` and `atto.core/*.atto` aliases
resolve to the same module; unrecognized or traversal paths fail closed.

The .NET typed source/CIL linker currently admits only a **scalar**
`int`/`bool` contract. Exposed methods are checked index/range predicates,
cursor calculations with explicitly named -1 exhaustion sentinel, min/max and
ordering. These are arithmetic policies, **not** validated memory borrows,
owned arrays, `Slice<T>`, `Iterator<T>`, `Ord` or allocation-free runtime
proofs.

The unmodified full array/callback source programs are kept separately in
`core/experimental/*_array.atto` and the prior high-level sample fixtures
are intentionally not compiled by this scalar linker. Module import/source
identity and typed CIL guest execution are tested in
`verification/Ores.Net.Tests/SourceModuleLinkingSelfTest.cs`. Check the
exact-head workflow output; source presence alone is not parity evidence.
