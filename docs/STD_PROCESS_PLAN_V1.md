# .NET std.process implementation — source-first draft


## Public API naming: asynchronous by default

In AttoLang, non-parking asynchronous operations are the **default** and
their names have no `_async` suffix: `spawn`, `collect_output`, `wait`,
`terminate`, and `kill`. The call sites may use `await child.wait()`
and `await child.collect_output()`. An intentionally synchronous/blocking
variant, if ever exposed, **must** end in `_blocking`. This is a public
semantic contract, not evidence that this backend implements OS process
launch or guest execution. Browser WebAssembly and synthesizable HDL remain
unsupported unless a separately authorized host provider exists.


## Public library path contract (2026-11-22)

Atto's preferred public import is `std/process`; `atto.std/process`
is the explicit, disambiguating built-in import. Apply the same rule to
`std/process_policy` and `atto.std/process_policy`. Do **not** publish
`ores.std/process` or `ores.std/process_policy` as new import names.
The two accepted spellings must resolve to the same compiler-owned module
identity and source, not two copies or an unchecked filesystem path.
The `atto.std/*` root must be reserved against local-package shadowing.

This backend's current relative fixture imports and source-only policy
**do not establish that either hosted import alias is implemented** in its
compiler. Resolver registration, positive/negative guest tests and an
exact-head execution receipt remain required before calling namespace
parity implemented.


`stdlib/process_policy.atto` is the .NET backend's independent guest-side
policy source. It validates stdio modes, spawn shape, child resource
reservations, bounded capture, monotonic deadlines, kill escalation and exit.
This code does **not** launch a process or confer authority.

## Runtime bridge still required

- Export `await process.spawn(Command)` as a typed
  `Result<ChildProcess,ProcessError>`, with an owned generation-bound handle.
  Never expose unvalidated raw OS PIDs or .NET `Process` objects to a guest.
- Use AOT-compatible `System.Diagnostics.ProcessStartInfo` with
  `UseShellExecute=false`, explicit `ArgumentList` and environment
  construction. Do not parse a command string as a shell.
- Separate the process-start submission from actor carrier threads.
  Use `WaitForExitAsync` with asynchronous stdin/stdout/stderr streams.
  Pump stderr concurrently with stdout to avoid pipe backpressure deadlocks.
- Enforce the actor/isolate RUN permission, scoped executable grant and all
  cwd/env/file permissions at the authoritative host boundary. Guest
  policy booleans cannot authenticate a capability.
- Supervise process termination, explicit timeout and output byte limits.
  Cancellation must stop or transfer ownership of the child and await
  reaping; cancelling only the waiting task does not kill a child.
- Use Windows Job Objects for bounded child trees when needed; on Unix use
  owned process groups. Never assume killing one PID kills all descendants.
- Keep handle and pipe ownership explicit; close exactly once. Record
  operation failures independently from ordinary process exit codes.

Raw `fork()` is not a portable public operation. A multithreaded actor
runtime must never continue executing its managed runtime in the fork child.

`tests/fixtures/std-process-policy-v1/main.atto` is source-only; expected
return 42 if compiler/typechecker import and execution are supported.
**NOT VERIFIED**: actual .NET guest compiler, Native AOT, process spawn,
pipe I/O, cancellation and reaping.
