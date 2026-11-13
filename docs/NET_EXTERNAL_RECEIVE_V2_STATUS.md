# Closed external receive source lowering — .NET v2 (draft)

This is a deliberately small, compiler-verified, executable NativeAOT actor handler slice, not general declared-actor parity. The language is AttoLang, and every new guest source is a .atto file.

## Inputs and semantics

The opt-in command emit-external-counter-csharp PACKAGE_ROOT --out path/GeneratedExternalCounterActor.g.cs accepts the canonical v2 Counter manifest and verifies five independently lexed .atto source units under src/counter/:

- A public actor declaration implementing the nominal CounterContract
- The actor contract and out-of-line scalar identity method
- A separate actor state declaration with one signed-i64 field
- A private external mtd receive with owned ActorMail<int> mail
- A critical-write that adds mail.value, critical-read that sends the resulting field, and a source-owned STOP condition

An ordinary method cannot masquerade as mailbox receive. The binder rejects unverified effects, state escape, shadowed receivers, wrong fields or namespaces, arbitrary expressions, malformed comments, invalid UTF-16, spaced critical modes, and unexpected signatures. It uses strict source/token budgets. The generated handler reads exactly eight little-endian bytes, uses actor-native state and a checked synchronous activation, sends a typed 16-byte supervisor reply, and triggers STOP from the verified source condition.

## Limits

The canonical asynchronous main.atto is intentionally NOT compiled. This is receive-handler lowering only, not the general package linker, shipping scalar CIL actor backend, Java actor-system parity, or independently verified native execution. The generated C# golden is compiled into the test executable and native probe, not emitted dynamically at runtime.

Contract source and all seven embedded units must first pass the repository's identical-source integrity tool, followed by materialization into a new directory. The CLI checks the canonical v2 manifest and prohibits symlink/reparse source roots/files before atomically writing output. Production source actor admission remains unchanged.

## Evidence gate

On Linux and Windows: exact source head/tree checkout, materialization and source checks, actual .NET emission from the complete canonical corpus, byte-for-byte golden comparison, complete managed tests, full NativeAOT publish and three native executions. The native probe requires 1|3|6|10, a second actor with separate state, STOP fencing, malformed-mail rejection, and fresh actor survival. Only runner-allocated nonzero steps count. Independent review and approved private-source transfer policy remain mandatory; zero-step source CI and Node contract-only checks are not native proof.

## Detached Copy scalar ownership update (2026-11-13)

The canonical shared Counter receiver now binds `val int snapshot = self.handle_state(critical-read |s| -> s.total);` then executes `self.send(snapshot);` only **after** the critical-read loan ends. The .NET compiler rejects the older `self.send(s.total)` read-scope variant and any state-view escape, mutable snapshot, wrong return type, direct send from within the loan or suspension. The source emitter now performs `state.AddInt64(...)`, then `state.ReadInt64(...)` (the runtime read acquires and releases a real actor-owned `ActorArenaReadLoan`), then writes the detached i64 to the reply mailbox. No actor-state loan is carried into `runtime.Send`.

This is still one closed lexical profile; it is not generalized lifetime inference or compiled actor CIL. Managed/Linux/Windows actual execution evidence remains mandatory.
