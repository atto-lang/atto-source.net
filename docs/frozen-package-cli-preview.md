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
