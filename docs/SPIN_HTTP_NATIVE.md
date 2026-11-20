# Spin HTTP: .NET Native AOT implementation track

The end state is execution of the actual shared `oreslang-spin` and
`oreslang-http-routing` Oreslang sources on .NET and LLVM, including fresh
shared/isolated request actors and exclusive exchange ownership. Java is a
reference for behavior, not a runtime fallback.

## Implemented .NET host transport prerequisite

`runtime/Ores.Net.Runtime.Http` is a separate, optional Native-AOT-compatible
provider built on Kestrel HTTP/1.1. The OS-independent actor/memory runtime does
not acquire an ASP.NET dependency. Only the HTTP provider references the framework.
No JIT, reflection-based guest dispatch, dynamic CIL loading or Java bridge is
added to the released Native AOT probe.

The transport owns connections and HTTP framing. Per-request opaque tickets are
bound to a destination `ActorRef` and generation. Claim requires the exact live
actor turn; a wrong actor cannot consume a legitimate ticket. Moving revokes
sender aliases before delivery. Request body bytes are copied into the receiving
actor's private unmanaged arena and the temporary transport copy is cleared.
Both shared and private routes have one writer and one pending response operation.

The provider uses async request-body and response I/O, bounded request admission,
body/response sizes and deadlines. Actor carriers do not wait on sockets or tasks.
A response lifetime pin is retained until physical I/O retires. The HTTP callback
releases its `HttpContext` after response completion, while separate retirement
tracking retains admission until the actor finishes or its deadline retires it.
This preserves HTTP keep-alive while bounding handlers that omit actor end.
Shutdown cancels, drains native I/O and actor retirements, then releases resources.

This is **host-provider execution evidence**, not source-level Spin conformance.
The compiler's shipping and experimental source gates remain unchanged. It does
not yet lower the real Spin source or expose HTTP capabilities to guest programs.
The host resolver callback is a compiler integration seam, not an alternative
implementation of the Oreslang route library.

## Reproduce the transport probe

Use .NET SDK 10 and the native compiler/linker for the host RID:

```sh
dotnet run -c Release --project verification/Ores.Net.Tests
dotnet run -c Release --project verification/Ores.Net.HttpProbe
dotnet publish verification/Ores.Net.HttpProbe -c Release -r osx-arm64 \
  -p:PublishAot=true -o artifacts/spin-http-native
file artifacts/spin-http-native/Ores.Net.HttpProbe
artifacts/spin-http-native/Ores.Net.HttpProbe
```

The HTTP probe starts real ephemeral loopback listeners and verifies fresh
shared/private actors, body ownership and transfer, sender revocation, wrong
owner and duplicate claim denial, retained-turn rejection, one response writer,
HEAD, body and response bounds, concurrent single-carrier progress, fault
containment, deadlines, admission retention/recovery and zero native arena leaks.
The existing async-only and blocking-wait scans include all runtime providers.
The local Native AOT result is macOS ARM64 evidence; Linux and Windows require
their own published binary executions.

## Remaining source-level work (goal remains open)

1. Resolve/import and type-check the real Spin/router module graphs with pinned
   source identities, without source flattening or backend-specific rewriting.
2. Lower owning classes, immutable records, receiver methods, arrays, strings,
   `Result`/`Option`, route snapshots and typed handler slots into static CIL.
3. Lower fresh shared/private actors and async/await to owning actor continuations;
   cancellation and I/O completion must resume through the actor scheduler.
4. Bind source `http.listen`, accept, claim, metadata, body and response APIs to
   the provider with source/actor capability admission and runtime generation checks.
5. Add streaming/chunk I/O, strict path decoding, captures/query/cookies/headers,
   middleware, protocol-generated responses, pooling and supervision as required
   by the actual Spin integration corpus. Preserve single-owner and lifetime rules.
6. Run the real unchanged Spin/router suites on .NET Native AOT and LLVM native
   output, including the common Java reference behavior and all ownership negatives.
7. Record exact source/compiler/runtime identities and executable evidence before
   changing backend manifests to supported. A host probe or rejection is not parity.

LLVM owning-local and control-flow work is proceeding in the existing
"Plan the Oreslang Spin HTTP" chat; this .NET checkout avoids its active files.
