# Actor input readiness and bounded host channels (host substrate v1)

Status: host-runtime integration only. No `.atto` guest compiler lowering, OS descriptor integration, distributed mailbox transfer, or NativeAOT receipt is implied.

## Authority and entrypoints

- `ActorRuntime.SpawnWithIo(kind, mailboxHandler, onInputReady)` mints an immutable `ActorRef` and a host-only `ActorIoHostPort`. Only callers holding this port can feed `io.in`, signal input EOF, read `io.out`, and observe output EOF.
- On an actor turn, `ActorContext.TryReadInput`, `InputEof`, `TryWriteOutput` and `CompleteOutput` require an active arena turn and current actor generation. A retained context does not confer post-turn authority.
- The older two-argument `SpawnWithIo` remains a manual-wakeup compatibility path; it does not register a callback.
- Host ports are local, non-serializable capabilities. Weak runtime references keep host-held ports from pinning a retired actor, its arena, or sibling actors in memory.

## Scheduler / mailbox semantics

- Every admitted host input record and the **first** input EOF publishes (or coalesces into) a pending readiness signal; no fake empty DATA message is sent into the actor mailbox. A burst can create at most **one pending input-ready bit per actor**, never one scheduled callback per queued record.
- A single scheduled actor-domain token exclusively runs either a DATA mailbox handler or an I/O readiness handler. Both enter `ActorArenaTurn`. If both are waiting, they alternate. The limit per dispatch is `ActorRuntimeOptions.MessagesPerDispatch`.
- The readiness handler may drain multiple frames; at most one pending input-ready bit exists for each actor even if 1,024 records have been admitted. A readiness invocation can find an empty queue when an earlier invocation already drained it.
- If coalescing leaves buffered input after a callback **actually consumed at least one frame**, the actor is rearmed once. A callback that refuses to consume input is not busy-spun; further readiness needs a new host input event or an explicit future retry policy.
- Regression tests verify a 256-frame burst causes exactly one callback when that callback drains the queue, while another 256-frame burst with a non-consuming callback does not burn carrier time with repeated callbacks. Both paths must refund outstanding I/O records on STOP.
- No dedicated I/O thread or CONTROL carrier is created. User callbacks execute on existing PRIVATE/SHARED/UNTRUSTED carriers. They must not block; callers must not assume automatic socket, file or pipe readiness here.
- `ActorRuntime.Stop` closes DATA admission and cancels queued I/O signals immediately without requiring any mailbox capacity. A live physical turn may complete; I/O-owned bytes are scrubbed at physical finalization, *not* when STOP is first requested.

## Resource accounting

- Every `ActorIoEnvelope` has independent four-MiB per-channel limits and at most 1,024 queued records per channel.
- `ActorIoBudget` additionally bounds queued frames/bytes **across all actors** in one runtime. Defaults: 64 MiB bytes and 65,536 records; even a zero-byte frame consumes one record credit.
- `ActorRuntime.IoQueuedBytes` / `IoQueuedRecords` are current **managed pending** queue telemetry, not a total process RSS bound, OS socket buffer measure or guest heap quota. Credits return on drain/disposal; already-delivered caller-owned data is excluded.
- Input/output half-close is directional; previously admitted queued records remain drainable. Repeated host EOF is idempotent and does not manufacture unbounded wakeups. Failed admission never consumes a credit.

## Required executable receipts

```sh
dotnet build verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release -warnaserror
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
```

Expected markers when actually executed by the source-owned workflow:

- `ACTOR_IO_ENVELOPE_RETIREMENT_PASS`
- `ACTOR_IO_HALF_CLOSE_PASS`
- `ACTOR_IO_HOST_INTEGRATION_PASS`
- `ACTOR_IO_GLOBAL_BUDGET_PASS`
- `ACTOR_IO_READY_SCHEDULER_PASS`
- `ACTOR_IO_WAKEUP_COALESCE_PASS`
- `ACTOR_SUPERVISOR_CONTROL_RECEIPT_PASS`
- `ACTOR_PIPELINE_MAILBOX_CONTROL_PASS`

Use the exact commit HEAD/tree, nonzero executed steps, and original runner logs; zero-step failures mean **NOT_EXECUTED**. These markers describe .NET host behavior only. A separate compiler/NativeAOT **guest** fixture must exercise the `.atto` equivalents before parity is claimed.

## Remaining work

1. Execute the source-owned tests and actual NativeAOT compilation in admitted CI; independently review and merge the PR after the proof.
2. Implement source-level `.atto` lowering for `io.in` / `io.out`, including borrow/ownership checks, EOF and backpressure, across the Java/LLVM/.NET/Wasm/Forge backends.
3. Bind readiness to nonblocking OS I/O completions with event-loop suspension, cancellation, supervisor generation fencing and independent quotas. Never block an actor carrier.
4. Implement typed supervisor/actor and actor/actor pipeline conformance with sender identity, fanout/backpressure receipts, failure injection and negative mutation tests.
