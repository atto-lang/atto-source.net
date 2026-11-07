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
