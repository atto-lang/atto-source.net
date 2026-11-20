# Closed-world public metadata / NativeAOT reference — task #34

`AotPublicTypeCatalog` is a copy-on-admission, immutable, explicitly
constructed descriptor table. It never invokes CLR runtime reflection,
`System.Reflection.Emit`, dynamic assemblies, runtime proxies, object
introspection, actor-private heaps or unmanaged arena pointers. It admits
at most **1,024 types**, each with up to **128 public fields and 128
methods**. All symbol names are bounded ASCII canonical identifiers.
Overflow, unsupported field kinds, duplicate method arities and unknown
types reject without a reflection fallback. These bounds are host-memory
admission controls, not a proof of zero-GC guest execution.

**Trust boundary:** the typed AttoLang compiler, not the C# class name,
must prove declared visibility and emit only approved, reachable public
descriptors. A caller supplying arbitrary values to this reference class
is **not** a trust boundary. Host reflection and unsafe state-container
access elsewhere in the runtime remain separate audit surfaces.

## Verifications requested by the workflow

`.github/workflows/aot-metadata-substrate.yml` pins the exact PR head and
SDK installation action. On a **working** Ubuntu runner, it should compile
and run the .NET 10 managed negative-test executable, publish a Linux x64
self-contained NativeAOT binary, and execute it with dynamic-code
generation disabled. The binary is a **host-only reference smoke** and
must print `NOT_GUEST|NOT_TRIM_VERIFIED`; merely adding workflow steps does
not mean a compiler ran.

**Current blocker (Nov 8, 2026):** the source-org run **42507046191**, job
**128811816224** failed with `steps=null` before runner admission. Thus the
new compile, publish and binary execution paths remain **NOT_EXECUTED**.
See organization billing/payment-or-budget diagnosis in conformance issue
#315 and .NET security/runner issue #221. Source-free or off-organization
carrier tests cannot be substituted without explicit authorization.

## Not implemented / release hold

- No source `type_of<T>()`, `fields_of`, `methods_of`, `implements`,
  typed generic-closure proof or source-level compiler integration.
- No generated CIL descriptor table rooting, actual linker trim graph
  inspection, runtime-issued actor inspection capability or heap proof.
- No actual compiler-produced AttoLang guest, Linux + Windows two-target
  native guest execution, binary/hash receipts or independent review.
- No transfer of private source across organizations or change to branch
  protection is authorized by this change.

Keep the PR in draft until exact-head executed-step evidence exists, with
the same distinction between managed-host, native-host and compiled-guest
results. Companion tracking: issue #342 / conformance PR #318.
