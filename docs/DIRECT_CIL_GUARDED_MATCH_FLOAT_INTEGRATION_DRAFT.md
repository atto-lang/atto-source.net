# Atto .NET direct CIL: guarded match + signed-i64 division integration

**DRAFT / AUTHORING_ONLY / NOT_EXECUTED / no approval to merge.**

This branch integrates the bounded compiled guarded match and permission-gated
stdout from [#26](https://github.com/atto-lang/atto-source.net/pull/26)
with correctly typed `int / int -> float` from
[#191](https://github.com/atto-lang/atto-source.net/pull/191).
Neither parent PR is implicitly certified, rebased, approved, merged, or closed.

## Semantic intersection

- Source methods accept only `int` and `float` result types; `main():int`
  remains required. Parameters and match subjects remain signed i64.
- `int / int` is `float`: both operands are independently converted to
  binary64 using `conv.r8` **before** CIL `div`. There is no integer-div
  result truncation. Checked i64 subexpressions run before conversion.
- Float results propagate through calls and method signatures. Float arithmetic
  beyond a single signed-i64 division is **not yet lowered**; matching or
  printing binary64 is **not yet lowered** in this bounded profile.
- Guarded `match` arms have i64 results and i64 subjects, boolean guards,
  scoped binders, exhaustive unguarded fallback, single-evaluation subjects,
  and explicit reachability/return validation. A float-valued arm/subject is
  rejected as `E_TYPE`, not coerced or emitted with an incorrect CIL stack type.
- `stdio.println` accepts i64 with explicit `--allow-stdio` host authorization.
  A float result cannot be passed to it; `E_TYPE` and **no DLL** are required.
  A missing grant is `E_PERMISSION` before artifact publication.
- Negative semantic tests also require `E_DIVISION_FLOAT_RESULT` when float
  division appears in an int return or as part of int-only arithmetic.
- Signed minimum `-9223372036854775808`, overflow checks, and inability to
  silently accept ADT `on is` patterns are retained from the two inputs.

## Identical source fixtures retained from existing PRs

The original `fixtures/match-return-forms-v1/**` fixtures and original
`fixtures/cil/**` division fixtures are copied into this **same private source
repository** by their original Git blob SHAs. The existing #26 match/stdio
scripts independently check pinned fixture identities before executing.
The #191 general scalar and division guest scripts remain in the matrix.
Three new **reject-only** fixtures test the intersection:
`float-match-result`, `float-match-subject`, `float-stdio-nonint`.

## Required evidence before any merge

1. Exact-head compile and static/type errors: no unexpected C# compiler errors;
   all negative fixtures exit 2, match a stable diagnostic and emit **no DLL**.
2. Genuine generated CIL, managed CoreCLR and Linux x64 NativeAOT: signed
   binary64 divisions including negative values, checked integer overflow,
   signed minimum, guarded source matches, and permission-gated exact stdout.
3. Windows x64 CoreCLR + NativeAOT equivalent guest checks; record separate
   binary SHA-256 and exact fixture/source Git blob SHAs.
4. Independent eligible review, repository policy compliance, and source-local
   CI or a **separately authorized** private carrier plus exact immutable tree.
   Alternate source-free .NET runner proof is **not** authorization to export
   Atto source or substitute for compiler guest execution.

No private source, test corpus, DLL, or Git object is sent to a different
organization by this branch or PR. No source Actions job has certified this
candidate. Merge neither this draft nor its component drafts without evidence.
