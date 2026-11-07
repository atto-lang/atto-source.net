# .NET canonical .atto module linker adversarial qualification

The existing typed CIL/NativAOT source-graph self-test now adds **ten**
adversarial checks directly against `OresSourceModuleCompiler.Compile`:
duplicate import binding, private helper leakage, canonical registry namespace
fallback, explicitly mixed canonical/legacy modules, unsuffixed legacy
extension compatibility, path traversal, nested package authority,
explicit dependency root, BOM and 1 MiB source budget, and Unix symlink
redirection. It also independently compiles and inspects a mixed-source
graph with `.atto` entrypoint.

These cases do **not** implement `atto.core/numeric` registry resolution;
that namespace remains deliberately unsupported in this .NET compiler
until a compiler-owned root/canonical source parser and typed import ABI
land. NativeAOT execution and actual CI test passes are still not verified
while GitHub runner admission remains blocked.

## Semantic source mutation and cross-suffix execution

An actual imported helper mutation from `twice(n)=n*2` to
`three_times(n)=n*3` is compiled through the same typed linker,
re-emitted as CIL and evaluated as **52** rather than the old **42**.
The CIL bytes and source graph hash must both differ, preventing source
comment/hash-only mutation evidence from being misrepresented as
executable re-linking. Two additional CIL checks execute an explicitly
mixed `.atto` → `.ores` import and the intentionally legacy unsuffixed
`.ores` fallback. These are authored tests, not hosted CI results.
