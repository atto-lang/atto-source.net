# Typed actor reply host slice (LLVM / .NET)

**Status: HOST-ONLY; NOT EXECUTED for AttoLang guest parity.**

Normative proposal: https://github.com/atto-lang/atto-spec/pull/40
Typed receives/sends dependency: https://github.com/atto-lang/atto-spec/pull/28
Shared source-only reference model: https://github.com/atto-lang/atto-platform-conformance/pull/40

The message's nominal interface determines reply cardinality:
- OneWayMessage: mailbox admission receipt only; no pending reply slot.
- UnaryRequest<T>: exactly one T or terminal transport error. LLVM NativeGenerationalReplySlots remains the existing host unary primitive.
- StreamRequest<T>: an ordered bounded stream of T, then normal EOF or a distinct terminal error.

Source operations target.send(msg)?, (await pending.receive())?, target.request(msg) and reply_to(current_request) are **future compiler targets**. These host components do not parse or lower them.

## Local host-only slice

- LLVM: NativeActorReplyStreamsV1.hpp has authenticated Actor/SupervisorApplication endpoints, bounded inlined payload ring and slot slab, generation-fenced leases, nonblocking try_send/poll, Full backpressure, normal close, failure, cancellation, endpoint retirement and no generation wrap. Existing NativeGenerationalReplySlots.hpp stays the host unary primitive. No CONTROL role exists in either.
- .NET: ActorTypedReplySlots<T> keeps a static unmanaged T per closed generic specialization, bounded preallocated inline buffers, unary/stream modes, nonblocking polling/writing and Full/End/Failed/Stale statuses, actor/supervisor app capabilities, generation fencing and zero-on-retirement. NativeAOT-safe host code does not mean no CLR GC.
- Host handles are never guest messages or serialized capabilities. Ownership and Sendable/Serializable checking must precede any extension beyond the current restricted inline host payloads.

## Required later work

1. Resolve nominal message mode+T in compiler HIR/MIR, reject conflicting/mixed-mode messages and preserve ActorRef<ActorClass> identity.
2. Make request mailbox admission plus caller slot reservation atomic, with private per-envelope binding and caller-lane resumption; do not run guest code on completion carriers.
3. Implement true stream writer leases tied to actor-owned continuations, scheduling, bounded byte credits, cancellation, deadlines, actor restart and protocol generation fences.
4. Execute actual positive/negative .atto guest fixtures at exact source/compiler heads and separately prove native AOT, supervisor CONTROL isolation, ownership and cross-backend semantics.

## Host-only test entrypoints

LLVM: build with CMake and run ctest -R native_actor_reply_v1_host_only, or compile tests/native_actor_reply_protocol_v1_test.cpp directly against src/main/cpp using C++17, pthread, -Wall -Wextra -Wpedantic -Werror -UNDEBUG.

.NET: dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release includes ActorTypedReplySlotsSelfTest.Run(). NativeAOT publishing is a separate verification gate.

No host test establishes compiled Atto request-response, await/suspending guest frames, native AOT parity, or application/CONTROL route equivalence.
