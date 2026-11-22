# std/env v1 — .NET CIL/NativeAOT

This repository independently owns `stdlib/env.atto`. The source candidate deliberately uses native AttoLang declarations and is **not runtime parity evidence**.

## API contract

`get(name): Option<String>`; `has(name): bool`; `get_or(name, fallback): String`; `get_required(name): Result<String,String>`; `get_nonempty(name): Option<String>`; `get_required_nonempty(name): Result<String,String>`; `parse_bool(raw): Result<bool,String>`; `get_bool(name): Result<bool,String>`; `get_bool_or(name, fallback): Result<bool,String>`.

Absent and empty are distinct for `get` and `get_or`. Only explicit nonempty helpers treat an empty value as absent. Boolean grammar is exactly `true`, `false`, `1`, `0`; malformed values are errors even if a fallback exists. Errors must not embed key *values* or secrets. `get_required` makes absence a Result error; denied access is never converted to None or a fallback. No mutation, enumeration, or ambient environment access.

## Backend admission blocker

The current compiler has no verified std/env linking or capability-checked env.get/has host adapter. NativeAOT must use closed-world, key-scoped grants without unrestricted CLR environment interop.

A trusted host interface must validate key names, check both coarse isolate ENV capability and scoped grants including explicit denies **before** reading any key, bound memory/UTF-8 inputs, and use deterministic error conversion. Host-only emulation is not .atto execution. AOT/native runs must prove import binding, guest results, denial-before-read, no unauthorized effect or secret exfiltration, and target-specific UTF-8/OS casing behavior.

## Tests

- `pure_bool.atto`: target exit 42 after actual compiler import/link/run; it makes no environment call.
- `denied_fallback.atto`: with an explicit denial of `ATTO_ENV_SECRET_KEY`, the guest must fail before returning any value, including `fallback`.

**Evidence: NOT_EXECUTED.** Fixtures are source-level contract targets until exact-head compiler-backed material CI runs. Never represent these files as implemented host access or full parity.

## Controlled positive guest fixture

`positive_get.atto` requires a deterministic trusted host injection of `ATTO_ENV_EMPTY=""` and `ATTO_ENV_FLAG="true"`, with `ATTO_ENV_ABSENT` explicitly unset. Grant only scoped `ATTO_ENV_*` keys and explicitly deny `ATTO_ENV_SECRET_KEY`. The target is exit code 42, checking present-empty versus missing, nonempty filtering, required-key errors, strict boolean parsing and fallback. Do not use ambient process environment to synthesize a passing result; the compiler must execute the actual .atto guest. **NOT_EXECUTED.**

## New .NET host-side admission scaffold

`runtime/Ores.Net.Runtime/StdEnvHostSnapshotV1.cs` is a NativeAOT-compatible, **host-only** explicit snapshot adapter. The trusted embedder supplies values plus independent coarse ENV authority and key-scoped grants. Denies override allow rules; only granted values are copied; values are limited to 16 KiB of well-formed UTF-8 and secrets never appear in errors. `Revoke()` retires and clears retained values. `verification/Ores.Net.Tests/StdEnvHostSnapshotV1SelfTest.cs` runs from the existing verification test executable.

This is still **not** a guest language builtin: `std/env` import resolution, `env.get/env.has` IR lowering and compiler-backed guest/NativeAOT tests are outstanding, and no real guest effects may be claimed from this host-only test.

## Compiler import fail-closed regression

The existing `OresSourceModuleCompiler` explicitly rejects reserved `std/`, `atto.std/`, and `ores.std/` namespaces until compiler-owned import support is implemented. `verification/Ores.Net.Tests/SourceModuleLinkingSelfTest.cs` now creates a project-local fake `std/env.atto` and asserts six canonical/legacy aliases fail with the reserved compiler-owned namespace diagnostic **before** the file could be linked or shadow the standard library. This guard is deliberately **not successful `std/env` import support**.

The host snapshot test also rejects embedded NUL values in addition to malformed UTF-16 and oversized UTF-8. .NET verification and NativeAOT CI remain **NOT_EXECUTED** here because no admitted dotnet runner or local dotnet SDK was available.
