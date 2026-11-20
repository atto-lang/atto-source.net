# Restricted compiled Result/Trap preview (trusted only)

## Scope

This is an **opt-in C# source emitter** for a narrow `.atto` subset, not a
change to the production `Atto.Cil.Compiler` direct-CIL ABI, or a general
`Result<T,E>` implementation. The generated C# is compiled by Roslyn into
CIL at build time and may be published as NativeAOT; there is no runtime
Reflection.Emit, dynamic assembly loading, or interpreter. The compiler and
generated code are **not** multi-tenant isolates or a security sandbox.

The accepted source subset has a public, zero-argument
`main(): Result<int,Error>`, optionally preceded by one private,
zero-argument scalar or Result helper. A helper may return an i64, Ok(i64),
Err(Error(message)), or explicitly declare `throws Error` and
`throw Error(message)`. The main may `return trap helper()`, `Ok`, or
`Err`. All other source forms, including `?`, Option, nested generic
types, actors, cancellation, async, raise/panic, parameters, external
imports, and host calls, are rejected before emitting any guest artifact.

- Canonical `int` uses signed i64, including `long.MinValue`.
- `trap` evaluates one helper invocation and returns Ok(value) for scalar
  success, passes through a helper's existing Result directly (no
  Result<Result<...>>), and converts only the guest-owned ordinary throw
  signal to Err(original Error). Caught Error identity is reused.
- The handler catches only `GuestThrown`; host faults, cancellation, OOM,
  panic and raise are not converted into Result. The latter source
  constructs are not accepted in this restricted preview.
- Messages are a length-bounded ASCII alphabet without quotes,
  backslashes, or control characters. Names are strict snake_case.
  Source-size and token budgets apply before codegen.
- Generated trusted standalone programs print a narrow observation of
  their returned tag and payload. Console is in this **trusted test
  harness** only, not general guest I/O authorization.

## Reproducible gates

Run `bash scripts/test-result-preview-guest.sh` on Linux with .NET 8,
NativeAOT prerequisites and real nonzero-step CI. The script compiles
generated code with Roslyn, executes CoreCLR and Linux NativeAOT guests,
compares stdout, and checks negative no-artifact rejections. A separate
`ATTO_RESULT_CLR_ONLY=1 bash scripts/test-result-preview-guest.sh` mode
runs the same CoreCLR guest and negative no-artifact fixtures without
NativeAOT, emitting `ATTO_RESULT_NATIVEAOT=NOT_EXECUTED`. A CoreCLR-only
pass **never** qualifies the full NativeAOT gate.

The authoring environment lacks a .NET SDK; all new .NET build/CLR/AOT
tests are **AUTHORED_NOT_EXECUTED** until exact-head executed evidence.
The preview is not a direct-CIL result ABI and must not be reported as
such in conformance matrices.

## Overlap and next required work

- `atto-source.net #19`: independent host-only Result parser and
  fail-closed direct-CIL admission. No implementation files duplicated.
- `atto-spec/language/trap-result-v1.md`: normative Result wrapping,
  original Error identity, fatal/raise/panic bypass.
- `atto-platform-conformance #68`: .NET guest conformance is unproven.

Next: implement compiler-owned tagged Result in verified **direct CIL**,
typed error joins, matching, cleanup-aware `?`, TenantVM-safe Error
lifetime and physical guest isolation. The preview is only a draft
stepping stone, not production support or release certification.
