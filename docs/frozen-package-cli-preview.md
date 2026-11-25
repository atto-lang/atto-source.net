# Frozen package CLI preview

This opt-in lane is a bounded scalar compiler candidate, **not** the general package
manager or full AttoLang source linker. The default compiler path remains unchanged.
An operator supplies each ZIP and independently trusted lock SHA-256, expected registry
name and version. Never compute the production trust pin from an untrusted archive.

```sh
oresnet check-frozen main.atto --package provider.zip @org/project/package 1.0.0 "$TRUSTED_LOCK_SHA256"
oresnet compile-frozen main.atto --package provider.zip @org/project/package 1.0.0 "$TRUSTED_LOCK_SHA256" --out OresGenerated.dll
```

Here `oresnet` denotes the existing .NET compiler CLI. Compilation emits verified CIL, not a NativeAOT executable. The output is staged and moved only after successful admission and emission.

Supply additional packages using repeated `--package ZIP NAME VERSION TRUSTED_LOCK_SHA256`.
Only the explicit canonical .atto consumer and the in-memory source inventory are admitted;
there is no archive extraction, ambient source discovery, package fetching, dependency installation,
host permission override, or runtime alias object.

Archive contents use the existing bounded snapshot profile: exactly .atto.pkg.toml,
.atto.pkg.lock, and pinned .atto source entries. No directory entries, traversal,
duplicate names, unknown entries or unpinned source files. Limits: 8 MiB compressed
and expanded per archive, 66 entries total (64 source files plus manifest/lock),
8 KiB manifest, 32 KiB lock, 1 MiB per source, 64 KiB consumer, 16 supplied packages.
The transport is an implementation preview, not a normative registry archive format.
File paths are explicitly supplied by a trusted host operator, not by guest code;
the digest checks authenticate the bytes actually consumed and not the host directory.

```atto
import pkg '@org/project/package' as p;

pub fnc main() -> int {
    return p.network.answer(40);
}
```

The pinned provider can implement `pub fnc answer(int x) -> int { return x + 2; }`
under `use namespace network;`. Expected guest result: 42.

Still outside this bounded profile: general CLI source linking, transitive provider
dependencies, arbitrary declarations and control flow, and multi-file namespaces.
CI receipts must show real execution on the exact PR head/tree; queued jobs do not
establish compiler or native execution parity.

## Dependency closure preview

The candidate manifest can declare exact dependency versions; the corresponding lock table pins each dependency's complete lock SHA-256. For example, append to the manifest:

```toml
[dependencies]
"@org/project/b" = "1.0.0"
```

Append to the lock after `[files]`:

```toml
[dependencies]
"@org/project/b" = "<actual lowercase 64-digit dependency lock SHA-256>"
```

The example hash is a placeholder, not an admitted digest. Every supplied archive still requires an independent trusted pin. The manifest and lock dependency key sets must match exactly. Before any provider parsing, verify all supplied packages, reject absent dependencies, conflicting versions/lock digests, duplicates and cycles, and derive deterministic dependency-first order. A package may resolve only a directly declared edge, not a dependency merely reachable through another package. This profile permits at most 16 packages and 128 aggregate edges; unused supplied snapshots are checked too. Cycle diagnostics precede edge digest mismatch checks on structurally cyclic malformed input. These tables are an implementation preview, not yet a normative registry lock format.

## Direct provider-to-provider calls (bounded candidate)

In dependency-first order the compiler now parses the original authenticated provider
source with the existing structured frontend. It accepts the explicit single-function
selector through a declared dependency's exported subpath:

```atto
use namespace network;
import fnc answer as next from pkg '@org/project/b/api';

pub fnc answer(int x) -> int {
    return next(x) + 1;
}
```

The owner must directly declare B and pin its exact version and lock digest. The
subpath `api` must be explicitly exported by B, and `answer` must be public.
Imports remain local bindings, not re-exports. A dependency reached only through B
does not authorize the owner to import that dependency directly. All source-owned
functions get separate compiler-owned method identities; calls go through the typed
CFG verifier and static CIL emitter. The candidate eagerly checks all supplied package
providers in deterministic order, with a maximum of 16 namespace providers and 64
linked methods. No filesystem fallback or runtime namespace object.

CI includes a three-package A -> B -> C candidate guest expected to return 42,
order-independent CIL emission and four negative checks. NativeAOT is proven only
when the standalone native binary actually executes on the exact reviewed head.
This remains a bounded scalar lane: general namespace imports within providers,
grouped selections, relative imports and general-purpose CLI linking are not admitted.
