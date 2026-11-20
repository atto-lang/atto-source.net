# Executable typed module linking for Spin

The opt-in `check-modules` / `compile-modules` path resolves and links original
source units as typed ASTs into independently verified CFG/CIL methods. It does
not concatenate, flatten or rewrite Oreslang sources. This is the first executable
int/bool function slice of the module path needed by the actual Spin/router sources.
The existing scalar and structured commands keep their original import rejection.

```sh
dotnet run -c Release --project compiler/Ores.Net.Compiler -- \
  compile-modules examples/modules/main.ores --root examples/modules \
  --out artifacts/modules/OresGenerated.dll
```

Each source unit keeps its local callable scope. Public direct function imports
and public qualified functions of named `define module` declarations resolve to
compiler-owned MethodDef identities. Private helpers cannot be called from other
units, and two modules may contain same-named helpers without collisions. Method
arguments and return values use the checked existing int/bool ABI. Cycles,
forward/recursive helper calls and unimplemented nominal/effect lowering reject.

The caller supplies one explicit `--root` and up to fifteen `--dependency-root`
options. Only explicit relative `.ores` imports are implemented. Nested package
markers require a separately authorized source root; reparse/symlink source paths
reject, including dangling package markers. No environment search paths, package
installation, foreign imports or network fetches are used. This is not the full
`+/`, `pkg:` or general import parity contract. Compile from a stable trusted
checkout: path checks are not descriptor-relative filesystem race confinement.

The returned witness hashes the complete admitted UTF-8 graph (paths relative to
the explicitly ordered roots plus SHA-256 of source bytes). Moving the graph to a
different machine directory preserves graph identity and emitted CIL. Source
edits, including transitive helpers, change the graph hash. Limits are 64 units,
32 import edges of depth, 1 MiB per source, 8 MiB total and the existing 64-helper
verified ABI bound. Unsupported source cannot publish a successful partial module.

Checked-in Oreslang sources and reviewed JSON contracts require LF on every
platform, and CI compares
each working-file Git object hash without filters to its committed blob before
compilation. This prevents Windows line-ending conversion from silently changing
the cross-platform source graph.

## Native execution

`examples/modules/main.ores` imports two same-named methods in separate module
scopes, with a transitive direct import, and produces 42. `overflow.ores` calls an
imported checked-i64 helper and must exit with the host's arithmetic trap status.
`reject-class.ores` preserves the still-unimplemented nominal lowering gate.

The `Source module Native AOT execution` workflow builds the **compiler itself**
as Native AOT, executes that compiler on the unchanged graph, publishes the
statically linked host as Native AOT, executes it and checks imported overflow.
It targets Windows x64 and Linux x64. Local macOS diagnostic verification uses
`verification/Ores.Net.ModuleProbe` with the same generated CIL; it does not change
the shipping compiler/host RID gates.

## Actual Spin frontier

Running the new checker on `oreslang-spin/examples/mixed.ores` with the Spin and
http-routing roots resolves the three-unit graph, then fails at the router's
first nominal class declaration. This is progress beyond blanket import denial,
not a full-framework pass. Owning classes/strings/arrays, actor/async lowering and
HTTP intrinsic bindings remain required for the user's LLVM/.NET Spin goal.

Current Java main `3210aeff07597035a4ed815ebbdf39720a3266c1` also rejects the
unchanged module fixture's sum of declared-int imported function calls with
`operator '+' needs numeric operands`. That is recorded as a reference inference
gap; the fixture was not rewritten per backend, and the .NET result is not
advertised as cross-backend parity proof.
