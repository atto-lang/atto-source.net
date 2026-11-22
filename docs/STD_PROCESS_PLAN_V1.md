# .NET std.process implementation — source-first draft

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
