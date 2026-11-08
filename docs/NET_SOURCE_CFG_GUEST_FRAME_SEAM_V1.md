# .NET source-derived CFG → terminal actor guest-frame ABI v1

**Status: source-authored, execution pending.** Stacked on integration draft
[#411](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/411).
This is NOT source-level `define actor` lowering, async suspension,
NativeAOT verification, native carrier-stack switching, or a successful
compiler-backed AttoLang actor execution receipt.

## What is now connected

1. A real `.atto` scalar fixture,
   `contracts/actor-guest-frame-seam/v1/scalar42.atto`, is parsed through
   `OresStructuredCfgLowering.Lower`, independently verified as typed CFG,
   and transformed to a bounded `CfgActorScalarFramePlan`. The compiler
   supplies immutable scalar SSA type tags and frame/entry-block sizes.
2. The new `ActorScalarGuestFrameAbiV1` uses those declared type tags to
   issue an ATF1 native frame through **`ActorContext.GuestPushStackFrame`**.
   Typed i64/bool read, write, PC and pop all use the **terminal guest**
   read/write/pop calls. It reuses the existing actor-owned native
   `ActorFrameStack`, with no second allocator and no raw pointers.
3. Type-tag, header, uninitialized-slot, noncanonical-bool, and invalid-PC
   errors latch the current actor's guest-frame fault. A C# embedding
   catching one of these exceptions cannot convert it into successful
   dispatch. Stale/off-carrier contexts are authenticated before the latch
   is changed: a stolen context cannot poison an unrelated live actor.
4. `ActorVerifiedCfgGuestFrameSelfTest` tests the source-derived scalar
   layout and `42` value across the guest frame. Actor A fills a **64-byte
   logical stack** with two 32-byte activations, catches a third-frame
   overflow, and must be terminal. Actor B survives in the same SHARED
   carrier pool, reads `42` and a canonical bool, and finishes with
   balanced frames. Stale frame authority and old actor refs are denied.
   The runtime can spawn a fresh replacement actor with a new ActorRef,
   whose native frame bytes must start zeroed. A separately corrupted ATF1
   header must terminate only its own actor and reclaim its quota.

All tests remain host-dispatch boundary tests because the compiler has no
actor declaration lowering. The numeric scalar value is extracted from
**verified compiler IR**, not hardcoded into the actor handler. The handler
itself remains C# trusted host code.

## Deliberate compatibility / limitations

- `ActorScalarFrameAbiV1` still exposes its explicitly failure-neutral
  **host** API for legacy integrations. It must not be used to implement
  compiler guest turns; use the new terminal guest API.
- ATF1 is the same wire-free native layout: 16-byte header, 16-byte
  little-endian type/initialization/payload slots. No new serialization
  capability or host closure/channel transfer is introduced.
- This stage does **not** preserve a stable supervisor ActorRef through
  restart. The current `ActorRuntime.Spawn` allocates a fresh actor ID.
  Replacing a faulted actor demonstrates fresh memory, not a stable
  incarnation restart. A future scheduler/supervisor contract is required.
- The compiler must eventually emit real `define actor`/method lowering,
  typed activation spills/restores and structured suspension/cancellation
  to use these methods. Neither call-site emission nor source actor
  execution is implied by generating `CfgActorScalarFramePlan`.
- Full AOT, host physical stack overflow containment, and cross-backend
  guest fixture parity remain unverified.

## Required evidence before readiness

Run `dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj
-c Release` at the **exact source PR head**, with nonzero executed CI steps.
Require the exact marker:

`PASS source-CFG-to-guest-frame-host-seam|value=42|quota_fault=1|header_fault=1|stale=denied|new_actor=1|guest_actor_atto=0`

Then run the Linux and Windows NativeAOT actor-memory probes and obtain a
valid independent approval. Existing failed Actions jobs with `steps=null`
are runner-admission failures, not compiler or runtime evidence. No private
source export to an external runner without independent authorization.
