> **AI agent instructions (organization-wide):** Before working on code, issues, or pull requests, read and follow the [organization `AGENTS.md`](https://github.com/atto-lang/.github/blob/main/AGENTS.md), including its **merge-first stealth-development policy**. Honor any repository-local `AGENTS.md`, explicit holds, and GitHub-enforced rules.

# atto-source.net

AttoLang compiler backend targeting .NET CIL and Native AOT on Windows and Linux.

## Canonical AttoLang naming and specification

This is the canonical [atto-source.net](https://github.com/atto-lang/atto-source.net) home. Use `.atto` source, `atto-*` names, and `https://github.com/atto-lang/atto-source.net.git` for Git remotes. Historical naming is not part of current usage documentation.

- **Normative contract:** [atto-spec](https://github.com/atto-lang/atto-spec), including the versioned ownership, borrowing, equality, lifetime, effects and module-constant contracts. New implementation work must use these specs, not old backend examples.
- **Guest parity:** [atto-platform-conformance](https://github.com/atto-lang/atto-platform-conformance) and [atto-compilers-parity-testing](https://github.com/atto-lang/atto-compilers-parity-testing). Pin the exact source/tree and toolchain; distinguish passed, failed, unsupported and not-executed outcomes.
- **Shared and target requirements:** [atto-common-source](https://github.com/atto-lang/atto-common-source) and [atto-target-profiles](https://github.com/atto-lang/atto-target-profiles). Parser-only acceptance and host-only tests do not prove guest execution.

## Scope

This repository is the AttoLang ecosystem home for:

- CIL lowering, Native AOT workflows, actor runtime integration, and platform contract tests.
- Compatibility with the [canonical language specification](https://github.com/atto-lang/atto-spec) where applicable.
- Evidence-based tests, benchmark results, and cross-backend conformance reporting.

## Architecture and compatibility

AttoLang is designed around actors, explicit ownership, and portable concurrency semantics. Backend-specific optimizations must not silently change the shared language contract. Runtime capabilities, target restrictions, and unsupported features should be documented explicitly.

## Building and testing

Consult the checked-in source tree and CI workflows for verified commands and supported targets. Add target-specific build instructions and exact-head execution results as they become available; do not treat a passing host-unit test as proof of guest-language conformance.

## Project status

This README describes the repository's intended responsibilities. Feature support and release readiness must be established from implementation, tests, and CI evidence rather than this description alone.

## Related repositories

- [AttoLang organization](https://github.com/atto-lang)
- [Language specification](https://github.com/atto-lang/atto-spec)
- [Platform conformance](https://github.com/atto-lang/atto-platform-conformance)
- [Commercial infrastructure](https://github.com/atto-co)

## Repository ownership and contribution routing

**Belongs here:** .NET/CIL backend, NativeAOT compilation, explicit dotnet host-library imports, actor arenas and lifecycle implementation, and .NET-specific compiler tests.

**Belongs elsewhere:** Java/Graal host imports, LLVM lowering and portable language semantics belong in atto-source.java, atto-source.llvm and atto-spec respectively.

**Security and portability boundary:** Do not equate NativeAOT with zero GC: require ownership/capability checks, bounded actor-local heaps/stacks, deterministic reclamation and executed negative tests.

AttoLang is the canonical project identity; use `.atto` and `atto-*` names in current documentation. Do not duplicate normative semantics from [atto-spec](https://github.com/atto-lang/atto-spec).

**Evidence gate:** record exact compiler/runtime head and tree, target/toolchain, nonzero executed steps, positive and negative guest results, and explicit passed/failed/unsupported/not-executed states. Host-only CI and `steps=[]` do not establish guest support. README scope is not a feature-completeness claim.
