# AttoLang source-first implementation guidance

These instructions apply to this repository's code changes, examples, fixtures, and AI coding agents. They supplement, and never override, repository security rules or review requirements.

## Source placement

- New AttoLang guest source, standard-library algorithms, sample programs, and guest-language regression fixtures use **`.atto`**. The `.ores` suffix is for legacy compatibility or explicit migration tests only; do not create new `.ores` features.
- Implement portable guest-visible behavior in AttoLang wherever it is expressible. Add only the smallest necessary host/compiler primitive when the language cannot safely implement that behavior itself. Do not disguise host-language emulation as actual AttoLang guest execution.
- Keep functions and methods in `snake_case`. Preserve actor ownership, sealed state, capability boundaries, deterministic errors, and full-AOT compatibility.
- If a necessary host-language implementation remains, document why guest code is not yet possible, which capability it supplies, and how it can be replaced. Do not mass-rename host compiler/runtime files to `.atto`.

## Evidence and security

- Separate source-only fixtures, host-model tests, compiler parse/typecheck, executable guest tests, and native AOT evidence. A zero-step workflow, synthetic receipt, or host-only test means **NOT_EXECUTED** for guest parity.
- Record exact fixture/compiler Git blobs and source heads when making parity claims; do not mark another backend as passing without its own executed result.
- Do not copy private Git objects to another organization simply because its runner has capacity. Respect source-side authorization, independent security review, approved private destination, and protected-branch requirements.

## .NET-specific responsibilities

- Use C#/.NET for CIL emission, compiler integration, low-level runtime interop and Native AOT bootstrap; prefer `.atto` for guest-visible library behavior and reusable algorithms.
- Require full Native AOT compatibility. Do not rely on runtime reflection, dynamic code generation or unbounded metadata discovery; use compiler-generated static descriptors when needed.
- Test actual compiled `.atto` guest behavior on both supported native targets where possible, and report hosted CLR execution separately from Native AOT evidence.

## AOT / zero-GC controlled-dynamism review gate

Follow [organization policy PR #38](https://github.com/ores-truffle-oreslang/.github/pull/38), [language-spec amendment PR #18](https://github.com/ores-truffle-oreslang/attolang-language-spec.md/pull/18), and [local checklist](docs/AOT_ZERO_GC_DYNAMISM.md). These are proposed design requirements, not evidence of implementation. Every compiler/runtime PR must declare the AOT mode and GC scope; reject uncontrolled runtime dynamism in sealed guest AOT before IR; generate proxy/virtual dispatch metadata ahead of time; prove allocation/closure/actor/task ownership, cycle management, bounded queues, cancellation and exactly-once cleanup.

Require positive and negative compiler-backed `.atto` cases plus actual exact-head guest execution and target-artifact/GC proof. Tests that run only the host, simulate a guest, or show zero executed steps are **NOT_EXECUTED** for guest semantics. Preserve current source-first, security, Rust-only tooling and review rules.
