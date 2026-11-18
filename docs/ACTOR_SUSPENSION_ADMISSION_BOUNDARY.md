# .NET actor suspension admission boundary

The current host `ActorRuntime` executes one synchronous handler at a time per actor and owns a distinct unmanaged actor arena. That is **physical-turn serialization**, not proof of Oreslang logical nonreentrancy across suspension.

A C# `async void` lambda can bind to a void-returning delegate. Without an admission guard, the handler can return at its first `await`, allowing `RunCell` to end the actor turn and process another message while the old continuation later resumes outside actor scheduling. That violates the Oreslang rule that a mutable actor handler remains logically nonreentrant across suspension.

This branch therefore rejects handlers carrying `AsyncStateMachineAttribute` before actor registration or native arena allocation. This is a host-surface fail-closed guard only. It is **not** the eventual Oreslang async actor implementation.

Real source suspension parity requires compiler-owned continuation frames, generation-fenced logical-turn ownership, cancellation/stop settlement, loan revocation before suspension, and scheduler-controlled resumption. The source compiler must not rely on CLR async-void behavior for those semantics.
