# .NET Native AOT `with` ownership gate v1

Both scalar and structured source lexers now identify contextual
`with {field => value}` after trivia and reject it with the compiler-owned
`WITH_BACKEND_UNSUPPORTED` category. Comments containing the word and
ordinary identifiers remain inert. No CIL or Native AOT reconstruction
is authorized.

`verification/Ores.Net.Tests/WithOwnershipAdmissionTests.cs` runs six
negative and two positive lexer controls through the *real* C# parser
implementations. It is not executable guest `with` parity. The canonical
guest conformance matrix is draft
`oreslang-platform-conformance` PR #693.

The next implementation requires typed `WithExpr` HIR, immutable value
allocation, `rt copy`/take ownership proofs, class field visibility,
drop-once unwind and Linux/Windows full Native AOT execution. Preserve
`self`, explicit `return`, and zero reflective fallback.

**NOT EXECUTED** on this PR head: .NET build, native AOT, guest `with` tests,
LLVM/Wasm/Forge parity. No source was transferred to another org.
