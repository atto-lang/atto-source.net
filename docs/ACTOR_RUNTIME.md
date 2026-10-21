# .NET integrated actor runtime

Status: **host-runtime integration substrate; source/compiler lowering pending**.

This layer connects the previously independent mailbox, actor-owned unmanaged
arena, and three runtime-wide dispatch domains into one serial actor cell.

## Actor cell

Every live actor owns:

- one bounded `ActorMailbox`,
- one bounded `ActorArena`,
- one logical `ActorKind` dispatch assignment: `Private`, `Shared`, or
  `Untrusted`,
- one serial message lane, and
- one handler entry point.

There is no thread pool per actor. The runtime owns exactly three actor execution
domains. Pool structures exist when the runtime is created, but carrier threads
are created lazily when the corresponding domain first receives work.

CONTROL is not a fourth carrier pool. Stop and fault are control-state changes on
the actor cell. An already admitted actor-domain dispatch performs finalization
after the current physical turn. An idle actor can finalize directly without a
CONTROL worker.

## Message and turn lifetime

`Send` validates local actor authority before publishing work. When an actor is
not already scheduled, the dispatch token is admitted while holding the actor
cell gate before the message is copied into the mailbox. The mailbox
prevalidates authority, size, message count and byte capacity before scheduling,
so a rejected message does not start a carrier. The mailbox revalidates under
its gate before publication. A newly created carrier
must acquire that same gate, so it cannot observe an empty mailbox and lose the
wake-up.

Each successfully admitted message receives exactly one `ActorArenaTurn`.
If turn admission fails after dequeue, the actor faults and the transferred
message buffer is scrubbed before its dispatch token exits. The handler sees
an `ActorContext`, not the underlying arena or native pointer. Scratch storage
dies on turn exit. Persistent allocations and actor-owned AoS/SoA/AoSoA shards
may survive into later turns through opaque handles.

The mailbox's delivered byte array is borrowed only for the handler turn and is
cryptographically zeroed after the handler returns, including handler failure.
The runtime also clears thread-local carrier identity even when finalization
throws. Retaining the
`ReadOnlyMemory<byte>` does not extend its useful lifetime. `CompletedMessages`
advances only after that cleanup completes.

Retaining `ActorContext` also does not retain arena authority. Every memory
operation validates the original `ActorArenaTurn`, so use after handler return
fails closed.

## Stop, fault, loans, and pins

Actor stop:

1. closes mailbox admission and scrubs queued undelivered payloads,
2. logically retires the actor arena, sealing new turns/allocations/loans/pins,
3. waits for the active dispatch token to leave its physical turn when needed,
4. removes the actor cell from the live registry, and
5. physically reclaims native capacity when read loans and lifetime pins are
   quiescent.

A handler exception retires only that actor in this slice. It does **not** yet
publish the typed parent/supervisor CONTROL failure envelope required for full
Oreslang supervision parity.

A read loan may survive a message turn and delays mutation/drop of its allocation
and physical arena reclaim. A lifetime pin may survive a turn or actor stop and
delays physical reclaim without granting memory authority. Releasing the final
loan/pin from another carrier may complete reclaim and refund the runtime native
budget.

## Data-oriented persistent state

`ActorContext.CreateShard` creates actor-owned AoS, SoA, or AoSoA storage.
Later turns use context-mediated `ReadShard`, `WriteShard`,
`IntegrateShard`, `CopyShardColumnInto`, and `ReleaseShard`. This keeps the
arena turn private while allowing ECS/data-oriented state to persist across
messages.

Only one physical layout is authoritative for a shard at a time. There are no
synchronized AoS/SoA/AoSoA shadow copies.

## Untrusted boundary

The `Untrusted` domain is currently a **scheduling/resource bulkhead only**.
Its separate global carrier pool and private native arena do not create a
hardware/process security sandbox. General untrusted guest execution must not be
claimed until the .NET backend has an explicit isolation design (for example a
process boundary or another proven containment mechanism) and conformance tests.

## Still pending for Java/LLVM parity

This integration does not yet implement:

- typed/generic `ActorRef<T>`,
- source actor declarations/spawn/send/stop lowering,
- send acknowledgements and `get_done()`,
- supervisor trees and typed CONTROL failure propagation,
- async continuation frames stored in actor-native memory,
- `await`, `select`, cancellation, and `*_blocking` suspension lowering,
- source `rt take`, `rt copy`, `rt share`, `rt borrow`, or
  `rt mut borrow`,
- shared-domain arenas and `critical-read` / `critical-write`,
- native strings/collections and zero-managed-allocation steady-state mailbox
  transport,
- authenticated distributed actor capabilities,
- process-generation hot-reload orchestration, or
- an untrusted execution security boundary.

Unsupported source features remain fail-closed.

## Verification

`ActorRuntimeSelfTest` covers:

- zero carriers at spawn time,
- PRIVATE/SHARED/UNTRUSTED domain placement,
- actor-owned persistent arena access,
- serial execution under a burst of messages,
- delivered payload scrubbing after completion,
- persistent SoA state across separate actor turns,
- rejection of retained/expired actor contexts,
- lifetime pins surviving stop and releasing from another carrier, and
- fault isolation between sibling actors,
- scrubbed buffers and fault accounting when arena turn admission fails after dequeue.

The Windows Native AOT workflow remains the authoritative compiler/AOT gate.
