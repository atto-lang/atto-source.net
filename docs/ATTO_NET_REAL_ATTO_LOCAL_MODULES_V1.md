# .NET NativeAOT canonical .atto linked local modules

This changes the **actual** `OresSourceModuleCompiler` typed module
linker and Native AOT workflow. The existing closed source-root graph now
accepts canonical `.atto` files as well as legacy `.ores`; both use the
same bounded UTF-8 reader, physical path identity, symlink/reparse checks,
cycle/type/visibility checks, and CIL emission.

The new executable four-file `.atto` graph uses explicit local
`"./atto-left.atto"` imports, compiles to a NativeAOT application, and
must return `42` on Windows and Linux CI. Additional compiler unit tests
check CIL evaluation, source hash changes, unsupported suffixes, missing
module files, and invalid UTF-8.

**Important limits:** This is not yet active `atto.core/numeric`
namespace resolution. Importing `"atto.core/numeric"` must continue
to fail closed rather than be resolved through ambient filesystem
paths. Local `.atto` modules have independent physical graph identities;
unsuffixed legacy local imports still select `.ores` for compatibility
rather than silently creating a second alias to the same logical unit.
Canonical snapshot at `contracts/atto-core-v1/numeric.atto`
is still not this source graph's executed module.

Evidence remains `NOT_EXECUTED` until NativeAOT workflows show nonzero
steps and independently checked exact source, binary, witness and output.
