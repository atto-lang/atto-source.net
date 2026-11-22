# .NET backend-owned core/math, core/hash and core/cell (v1)

Production CIL compiler `OresSourceModuleCompiler` registers three independent
AttoLang `.atto` sources as embedded resources and verifies their immutable
SHA-256 digests before parsing. Canonical `atto.core/*` and compatibility
`ores.core/*` import spellings share one compiler-owned unit identity.
Ambient source roots and traversal imports remain denied.

- `math`: bounded signed min/max, signum, inclusive closed-range predicate.
  Typed Result clamp, float functions, and abs(INT64_MIN) are deferred.
- `hash`: seed 17, multiplier 31, prime 1,000,000,007, byte fold with
  checked input; invalid=-1, noncryptographic and not a universal Hash trait.
- `cell`: only pure borrow-state policy. No Cell<T>/RefCell<T> storage,
  mutex/atomic lock, actual loan token or interior mutability authority.

The added ModuleInitializer test emits CIL from a real linked `.atto` guest,
invokes the test CIL interpreter, expects 42, and rejects fake Core paths.
Successful interpreter verification is *not* NativeAOT execution or general
linked guest parity. All tests currently NOT_EXECUTED pending exact-head
stepful CI and separately authenticated export approval if mirroring.
See Java #1770, LLVM #869, conformance #570 for sibling candidates.

## File namespaces instead of module wrappers

The three compiler-owned `.atto` libraries now begin with
`use namespace atto.core.math;`, `use namespace atto.core.hash;`, and
`use namespace atto.core.cell;` respectively. Their exported `pub fnc`
declarations are top-level; there is no `define module ... as ... end` wrapper.
The existing authenticated `import module math from "atto.core/math";`
compatibility form still resolves one compiler-owned source unit and applies
an internal export adapter, but does not interpret namespaces as ambient
imports, runtime objects, package rights, or filesystem access. User-supplied
`use namespace atto.core.math;` source remains rejected until the general
package-verified linker is complete.

The namespace grammar is a constrained first milestone only. The
`import * as math from "core.math";` built-in selector and the distinct
`import ... from pkg "third.party";` package selector follow the
unified static-imports design; neither general import form is implemented
by this CIL source linker. Arbitrary user-defined file namespaces remain
fail-closed until authenticated package-graph support exists.

## Canonical built-in namespace import (restricted CIL slice)

In addition to the legacy `import module math from "atto.core/math";`,
the compiler now recognizes the canonical static namespace selector:

```atto
import * as math from "core.math";
import * as hash from "core.hash";
import * as cell from "core.cell";

pub fnc main(): int {
  return math.signum_int(-19) + hash.initial_state() + 26;
}
```

Only these three exact compiler-owned names are admitted in the `import *`
form. The names after `as` are local aliases; they may not shadow each
other. Multiple local aliases of the same built-in resolve to the **same
pinned source identity**; exported symbols are checked by the typed linker.
The compiler still rejects wildcard `./`, unknown `core.*`, `std.*`,
`alloc.*`, and `pkg` imports, and never scans user filesystem directories
for a builtin. The legacy `import module` spelling remains compatible.

The associated CIL tests compile canonical and legacy entrypoints and compare
the immutable imported-unit SHA-256 identity, execute generated CIL with the
existing test interpreter, check alias de-duplication and deny wildcard
specifier traversal. **These checks remain unexecuted until admitted by
stepful CI on the exact PR head**; they are committed candidate tests, not
a claim that the compiled guest succeeded.
