# .NET scalar `define macro` stage-0 parity

**Status:** draft compiler change, not a tested full-language or Native AOT release.
Tracking: [#150](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/150).
Sibling implementation slices: Java/Graal [#2049](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/2049) and LLVM [#1026](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/pull/1026).

The production .NET single-function signed-i64 parser admits three narrowly typed, compiler-only expression forms common to the current Java/LLVM scalar macro slices:

```atto
define macro literal(Const<int> value) -> Syntax<Expr> {
    return quote(value);
}
pub fnc main(): int { return literal!(const 40 + 2); }
```

```atto
define macro identity(Syntax<Expr> value) -> Syntax<Expr> {
    return value;
}
pub fnc main(): int { return identity!(syntax expr 42); }
```

```atto
define macro twice(Syntax<Expr> value) -> Syntax<Expr> {
    return quote(unquote(value) + unquote(value));
}
pub fnc main() -> int { return twice!(syntax expr 21); }
```

The `twice!` template is **one exact, identifier-free expression shape**, not general `quote`/`unquote`: its body must have two direct unquotes of the same declared parameter and one `+`. The argument must expand to a single closed integer `Literal`, including a preceding nested `literal!(const 20+1)`. Two new compiler AST literal nodes are allocated, with distinct object identities; signed overflow from doubling is rejected before OresIR. Identifier, call and composite arguments, generated names, undeclared unquote slots, altered templates and a third splice all fail closed. The scalar entry grammar itself cannot accept parameters on `main`.

The declaration is validated (including when unused) and omitted from runtime AST. `literal!` visits only closed, checked signed-i64 literal/unary/add/subtract/multiply expression AST and emits a new literal node; `identity!` forwards the one caller-owned expression AST. All three forms expand before `OresIrLowering`, the standard verified OresIR, deterministic CIL emitter and statically linked Native AOT host. No runtime macro registry, `Reflection.Emit`, source-string replay, Eval, JIT or generated code execution occurs.

Hard bounds: 8 declarations per translation unit, 128 expansions, 512 visited `Const<int>` AST nodes and maximum evaluation depth 64. Signed overflow fails before CIL publication; unsupported macro parameter/results/bodies, public macros and unresolved macros fail closed. The source size/token/depth limits remain in force. Recursive macro calls cannot exist inside the three accepted declaration bodies.

**Not supported:** name generation/hygiene, checked explicit capture, templates beyond exact AST identity / literal quotation / the two-unquote closed-literal addition, declarations / imports / public macro exports, cross-module cache identities, macro argument types other than `Const<int>` or `Syntax<Expr>`, general `comptime`, float/bool/String/Symbol constants, actors, ownership-aware side-effect duplication, and expanded structured/linked CFG compilation. The experimental structured frontend and linked-import scanner explicitly reject all macro declarations and invocations, including the scalar and typed-template positives, until they have separately proven typed AST expansion.

**Exact-source execution gate prepared:** `bash verification/verify-macro-scalar-native-aot.sh`
uses all seven byte-identical shared stage-0 `.atto` fixtures from
[common-source PR #218](https://github.com/ores-truffle-oreslang/oreslang-common-source/pull/218)
plus a .NET stage-0-specific public-macro rejection fixture under
`verification/fixtures/`. The template parity extension adds **five**
source-byte-identical LLVM fixture vectors (`twice!`, nested `literal! + twice!`,
composite rejection, overflow rejection, and unresolved-name rejection).
LLVM also keeps its stronger separately authored test with a **bound**
`main(int value)` parameter; .NET's scalar entry grammar does not yet
admit a parameterized `main`, so that extra LLVM case is not in the
byte-identical shared set.
The gate checks the production compiler's Release tests; **five** separately
published Linux Native AOT guests whose stdout is exactly `42` followed by a
newline and whose successful exit status is **0**; Native AOT release layouts;
and **eight** negative fixtures times four CLI commands = **32** rejection
checks with no output artifacts. LLVM's native guest currently uses exit-status
42 instead; these host ABIs are distinct but their guest integer values agree.
The shell gate is **authored, not executed**. It must run within the source
repository or only on an independently authorized private destination.

**Verification required before merge:**
1. Build the .NET 10 source compiler on the exact branch head and run `dotnet run --project verification/Ores.Net.Tests`. Check the dedicated macro AST, checked OresIR, deterministic CIL bytes and Mono.Cecil entry evaluation assertions.
2. Publish the exact compiler and application with Native AOT for supported Windows/Linux RID(s), execute the generated guests and assert stdout `42` with exit status 0 (the native host's established value-output ABI), and capture source/tree, compiler, generated DLL, host executable and SDK hashes.
3. Run negative cases through the actual CLI `check`, `compile`, `emit-csharp` and `emit-fsharp`, asserting nonzero errors and **no output artifacts**; run linked/structured path denial cases.
4. Compare shared macro vectors against Java/Graal #2049 and LLVM #1026 at pinned heads. A backend's non-executing source checks, zero-step Actions job, or synthetic CIL smoke must never be recorded as executable cross-backend parity.
5. Obtain independent review and an authorized source-bound runner. A funded test organization does not, by itself, authorize export of private source.

Keep the PR draft and do not merge based on syntax-level parity alone.
