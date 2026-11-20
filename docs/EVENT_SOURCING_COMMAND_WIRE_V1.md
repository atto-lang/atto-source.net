# AttoLang .NET Native AOT — event-sourced command wire v1

Status: **AOT-compatible host codec**, NOT a durable actor or guest compiler
implementation. `runtime/Ores.Net.Runtime/DurableCommandWireV1.cs`
serializes 1–256 owned immutable domain events in the exact big-endian
format used by Java/LLVM, with bounded 64KiB events and request/reply,
1MiB total batch, and fail-closed malformed wire parsing. No reflection,
dynamic assembly loading or runtime code generation is required.

`verification/Ores.Net.DurableCommandProbe` is a focused executable host
probe: canonical two-event vector, bounds, malformed lengths/count/schema,
caller-alias isolation, and immutable reply/request envelope. CI requests
actual Native AOT on Linux and Windows; a CLR execution is not evidence of
Native AOT. New `.atto` event-sourced actor source remains unsupported by the
current scalar CIL compiler and must fail closed; none of these tests attest
to emitted actor guest code, SQLite journal durability, supervisor restart,
or transactional inbox reconciliation.

Remaining milestones: authenticated host storage broker, atomic event/inbox
transaction, actor-owned nonblocking completion, pure reducer + sealed state
lowering, exact-head real guest run, and conformance across all five backends.
Do not promote zero-step workflow failures or host-only probes as guest parity.
