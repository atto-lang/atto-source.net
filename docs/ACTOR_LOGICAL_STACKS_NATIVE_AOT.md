# Actor-owned logical frames for .NET Native AOT

Status: **runtime host implementation candidate, not yet compiled or executed on an exact-head runner**.

## What the current implementation actually provides

Each serial `ActorRuntime.ActorCell` has exactly one independent
`ActorFrameStack` and exactly one existing actor-private `ActorArena`.
The stack reserves a bounded slab **inside that arena's persistent allocation
region** on first push. It has no separate unmanaged allocator and does not
change the arena/process native budget accounting.

- The default logical stack budget is one quarter of the actor's persistent
  native capacity, capped at 64 KiB, unless `ActorStackBytes` is set.
  `ActorMaxStackFrames` defaults to 256, capped at 1024.
- `PushStackFrame` / `PopStackFrame` enforce LIFO. Frame contents are
  addressed through exact-issued actor-local handles, not native pointers.
- A new frame's contents are zero. Popping immediately scrubs its bytes.
  The backing slab stays reserved for the actor incarnation to avoid
  fragmentation in the non-compacting arena, and is scrubbed/freed on actor
  retirement. Frame handles are invalid forever after pop, including if
  the same offset is reused.
- Parent frames may be read or written while children exist, but cannot be
  popped until every child has been popped.
- The stack survives **physical actor turn** exit, so a new admitted owner turn
  may read a retained frame. An expired `ActorContext` cannot access it.
- Cross-actor frame use, off-carrier access and stale turn replay all fail
  through the existing `ActorArena` physical-turn and capability checks.
- Retirement blocks **new pushes**; active-turn cleanup and final arena
  reclamation follow existing quiescence gates. An explicit JSON converter
  denies serialization and deserialization of frame handles; no `ActorRef`
  message can grant native-frame authority. Other wire formats must enforce
  the same no-authority policy.

## What separate stacks mean (and do not mean)

These are **per-actor logical activation/continuation frames backed by
per-actor native guest memory**, not independent .NET machine stacks, new
OS threads, switched CLR call stacks or copied C# stacks. Carrier threads
continue to use ordinary Native AOT machine stacks. Actors do not pin one
carrier thread each.

An actual guest `await` must be compiled into an explicit suspended
continuation: spill *typed* live locals into these actor-owned frame bytes,
close the physical turn, then reenter a **new authenticated physical turn**
and restore them. No machine stack pointer survives suspension. This
runtime change supplies only the bounded storage and frame lifetime edge.
The compiler has not yet emitted those spills, typed layouts, destructor
schedules, unwind edges or resume tickets.

The current `ActorMessageHandler` remains synchronous. C# `async void`
must not be mistaken for an actor continuation. Source `define actor`,
`rt borrow`, `rt take`, `critical-read` / `critical-write`, typed
`select` and actor failure/supervisor lowering remain separate compiler
and runtime milestones.

This host feature does not prove zero GC: stack frame **bytes** are unmanaged,
but frame handles, actor metadata and CLR control flow are managed. Nor does
an in-process UNTRUSTED carrier domain provide an OS/tenant sandbox.

## Parity and acceptance

Java's `ActorHeapAdmission` currently denies unverified zero-guest-tracing
claims. LLVM's `STACK_TRACE_EXECUTION_STORES` separates ExecutionId,
ContinuationHandle and AllocationDomainRef, and explicitly reconstructs
logical history rather than retaining pthread stack pointers. The .NET stack
follows this *logical versus physical* distinction, without yet implementing
LLVM's native continuation trace ABI.

`ActorFrameStackSelfTest` exercises independent actor stacks,
cross-turn frame persistence, stale/foreign frame denial, LIFO,
stack/frame quotas, zeroed reuse, budget accounting and quiescent stop.

Required before parity certification:

1. Execute the changed .NET test host at this PR's exact commit and tree.
2. Execute strict Linux and Windows .NET Native AOT build and host tests; record
   the actual native artifacts, workflow/run/job IDs and non-skipped assertions.
3. Add compiler-owned typed frame descriptors, owner-checked spill/restore,
   generation-fenced single-claim resume, checked borrow/drop on every unwind,
   bounded recursion, supervisor fault propagation, and cancellation cleanup.
4. Execute the **same** `.atto` actor/await/select guest corpus under Java,
   LLVM and .NET. Report host-only and parser-only evidence separately.
5. Preserve private source and exact-tree security restrictions. No copied
   private source to an unapproved CI carrier, no synthetic certification.

Until (1)-(4) execute, this implementation remains **NOT_CERTIFIED**
for source guest, native-stack or end-to-end AOT actor parity.

## Runtime typed spill and single-claim resume ABI (stacked follow-up)

The runtime host additionally supplies immutable 1–128-slot `ActorStackFrameLayout`
descriptors. Each slot is eight bytes, little-endian, and is exactly `I64`
or `Bool`; bool values have canonical one-byte 0/1 encodings. Layouts copy
caller-provided arrays so later mutation cannot change an already-issued frame.
Raw `PushStackFrame` frames cannot be used as typed continuation spill frames;
typed frames refuse the public raw byte read/write calls even during a live
physical turn. Only canonical typed operations may touch their slots.

`SuspendStackFrame` only accepts the active LIFO top frame, freezes the entire
actor stack, and returns an exact-issued JSON-nonserializable
`ActorStackResumeTicket`. No frame reads, writes, pops, or further pushes are
allowed while pending. `ResumeStackFrame` requires a **new authenticated
physical turn** of the same actor incarnation and can claim the exact ticket
**once**. Foreign actors, expired turns, same-turn resumption, ticket replay,
and typed slot confusion fail closed. `CancelSuspendedStackFrame` scrubs the
top frame and revokes its ticket, including in an in-flight physical turn
while logical actor STOP prevents new frame admission.

These are **runtime interfaces and host regression tests**, not emitted from
AttoLang source. The compiler must still generate complete per-function frame
layouts and explicit spill/restore/drop/unwind edges; guest actor lowering,
verified await suspension, supervisor propagation, structured cancellation,
and distinct Windows/Linux native execution receipts are pending.

The `ActorTypedFrameResumeSelfTest` covers layout snapshotting, typed i64/bool
round-trip across physical turns, raw typed-frame access denial, stale/foreign
tickets, same-turn denial, single-claim replay, cancellation and zeroed reuse,
retirement unwind, and actor-local quota-fault containment with a surviving
peer. Source-org CI without nonzero executed steps is **NOT_EXECUTED**.
