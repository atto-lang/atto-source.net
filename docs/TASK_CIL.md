# Inline task and root task CIL

The normal `.atto` `check` and `compile` commands accept `sync task<T>` expressions for supported scalar and tagged result types. The body stays in the caller's verified CFG. Its explicit `return` or `^` branches to the inline continuation. The value form can read surrounding locals. `do sync task` executes a void body, can update surrounding mutable locals, and supports either fallthrough or an explicit void return.

Inline code runs on the caller thread and creates no task or Future. In a standalone root guest, `rt cooperate;` offers the current OS time slice and returns on that thread. A guest-scheduler handoff that can run another logical task on the same carrier still needs runtime integration. The compiler rejects an `await` without a same-thread continuation, unsupported effects and a void task used as a value. Writeback from a value-context inline task, inline postfix propagation, trapping an outlined inline task with side effects, and resource cleanup from general `defer` scopes still need verified lowering.

The explicitly selected `compile-task-i64` profile accepts `val Future<int> f = task<int> { return ...; }; return await f;` in a `susp` root `main`. Its capture-free arithmetic producer is compiled to CIL and scheduled by the bounded root-task host. The host observes its Future once and waits for physical retirement before completing. The existing `Future.from_task` wrapper form stays available in the opt-in profile. Arbitrarily many tasks, captured producers, suspension inside a producer, and actor task scopes require the general task emitter and scheduler integration.

`ci/test-task-inline-cil.sh` executes compiled inline cases and the separately scheduled typed child on CoreCLR or standalone macOS ARM64 NativeAOT. It checks that incompatible inline source emits no CIL.

## Guard integration boundary

Both ordinary guard forms require an enclosing callable return on every failure path. The inline-task and imperative-match local return machinery must not silently retarget those returns to a lexical join. This profile rejects guards inside those local-return scopes with `E_GUARD_LOCAL_RETURN_UNSUPPORTED` until nonlocal exit and cleanup are implemented. Guards in the enclosing callable can be composed with inline tasks and trapped Result values. The integration fixture covers both guard spellings, trap success and division failure, and an inline match. Both runtime lanes require diagnostic-specific check/compile denials and no guest DLL for the four unsupported lexical guard cases.

The preflight suspension gate distinguishes await inside a braced inline task from await outside it, without admitting either. The negative suite verifies nested typed inline bodies and that the diagnostic does not leak past the closing task brace. Diagnostic mismatches now print the fixture, status and captured compiler output.
