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
