# .NET Native AOT durable command inbox — draft host slice

The `DurableCommandInboxV1` runtime component is a privileged synchronous
SQLite WAL/FULL storage provider. It uses source-generated SQLite native LibraryImport,
bounded owned byte arrays, and deterministic AOT-compatible descriptors;
it does not create runtime IL, use reflection, JIT, or dynamically load guest
assemblies.

The provider atomically commits 1–256 contiguous event rows, journal head,
original canonical command bytes and original serialized reply. It enforces
an actor/tenant/slot fence and stores each operation ID exactly once.
After an authenticated new activation epoch, a retry with identical request
bytes can verify every retained journal event and recover the original
reply. Changed request bytes, stale epoch or missing journal rows fail
closed. Event framing is big-endian: count:u32, then repeated schema:u32,
size:u32 and exact owned bytes; max batch 1 MiB, request/reply 64 KiB.

`Ores.Net.DurableCommandProbe` is a dedicated **Native AOT host** executable
so this can be tested without taking a dependency on managed test-runner
reflection or JIT. The `sqlite3` native library must be provisioned as a
reviewed deployment dependency (Linux workflow only). Windows Native AOT
and static packaging still need separate runs.

IMPORTANT: This does not lower .atto event-sourced actors to CIL and does
not implement a compiler-enforced pure reducer, sealed state, supervisor,
real actor-turn storage worker, zero-GC actor heap, or original reply through
an executable .atto guest. Until real exact-head Native AOT execution is
recorded, both host execution and five-backend parity remain NOT_EXECUTED.

No private-source CI transfer is authorized by a source-free canary or the
existence of these workflows. Independent carrier approval remains mandatory.

## Native AOT marshaling hardening

The SQLite boundary uses `LibraryImport` methods generated at build time
and explicitly pinned UTF-8/path/SQL/blob buffers with `SQLITE_TRANSIENT`
for bound values. Runtime IL marshalling stubs and reflective metadata
discovery are not required. The test-only fault injector also uses
source-generated `LibraryImport`, not `DllImport`.

This must be tested in an actual Native AOT binary on Linux, and separately
on Windows with an explicitly deployed native sqlite3 library, before any
full-AOT capability is certified. An authored probe source is NOT a pass.
