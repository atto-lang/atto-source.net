# Async-only guest I/O contract (.NET Native AOT)

Status: **fail closed / implementation pending**.

An optional host HTTP/1.1 provider now exists in `runtime/Ores.Net.Runtime.Http`.
Its real-socket probe runs under Native AOT, with async-only transport and
actor-bound exchange ownership. Source-level HTTP/await admission remains
unsupported; see [Spin HTTP implementation track](SPIN_HTTP_NATIVE.md) for the
implemented prerequisite, evidence scope and remaining compiler work.

The .NET backend does not yet lower Oreslang async/await or expose guest
filesystem, console, socket, DNS, HTTP, timer, or process I/O. That limitation is
part of the safety contract: unsupported guest I/O must reject before CIL
emission rather than falling back to synchronous .NET APIs.

## Required runtime shape

- `core` remains OS-independent and has no host I/O.
- Guest-visible standard-library I/O must settle an Oreslang `Future<T>` or
  asynchronous stream and resume through the owning Oreslang scheduler.
- A guest/actor execution carrier may not execute blocking `System.IO`,
  synchronous socket, console, DNS, HTTP, process, or sync-over-async waits.
- Wrapping a blocking call in `Task.Run`, a thread-pool work item, or a
  dedicated thread does not qualify as native asynchronous I/O. If a temporary
  bounded offload is ever required for a platform primitive, it must be
  documented as such and must not be advertised as strict nonblocking parity.
- Native AOT compatibility is mandatory: no dynamic assembly generation,
  reflection-based runtime dispatch, or JIT-only fallback.
- Cancellation, deadline, EOF, partial transfer, backpressure, close races and
  physical resource retirement must be defined before a capability is admitted.

## Verification gate

`verification/Ores.Net.Tests/AsyncIoBoundarySelfTest.cs` runs automatically as
a module initializer in the existing verification executable. It scans only
`runtime/Ores.Net.Runtime` production runtime sources and rejects obvious
synchronous file, socket, console, HTTP, thread-parking and sync-over-async
constructs. Compiler CLI/build tooling is deliberately outside this scan because
the async-only assertion applies to guest-reachable core/stdlib/runtime paths,
not source-file compilation itself.

This static gate is defense in depth, not proof of functional async I/O. When
the first guest I/O provider is implemented, add executable single-carrier
progress tests and exact Native AOT tests before changing this status.
