# Direct CIL: typed `int / int -> float` (bounded guest subset)

**Status: DRAFT / AUTHORING ONLY / NOT_EXECUTED on this exact head.** This is a restricted compiler implementation; not full cross-backend floating-point conformance or NativeAOT certification.

## Normative direction and original contradiction

The owner-approved direction in [atto-spec #151](https://github.com/atto-lang/atto-spec/pull/151) is that `int / int` produces floating-point, not a truncated integer. Previously the standalone `Atto.Cil.Compiler` always returned signed i64 and emitted `ILOpCode.Div` on integer operands; `5 / 2` thus produced `2` rather than `2.5`.

The source parser now admits `fnc name(int a, int b): float` and `-> float` as **return types only**, with signed-int64 parameters and integer literals. The whole function call DAG is statically type-checked before any output artifact is written:

- `int / int` yields `float`, within functions declared `float`. The emitter generates `conv.r8` on **each signed-i64 operand**, then emits floating-point `div`, with a real ECMA-335 binary64 return signature.
- Function calls returning that `float` can pass it through another `float`-returning function.
- The entry point remains `pub fnc main(): int` and the static NativeAOT bridge calls it as before; existing checked i64 `+`, `-`, `*` semantics remain unchanged.
- Use of a `float` result as `int`, passing it as an `int` argument, implicit return type mismatch, or mixed/float arithmetic outside the admitted division profile **fails closed** before a guest DLL is created. We do not silently insert narrowing, load an invalid IL stack type, or generate integer division and post-convert.
- The exact signed minimum `-9223372036854775808` is a legal i64 literal; the underflowing `-9223372036854775809` remains invalid. This hardening was preserved from the independent same-branch update.

## Compiled-guest and negative verification gates

The source-backed `fixtures/cil/float-division.atto` exercises `5 / 2 = 2.5`, negative operands, i64 minimum, float-returning helper calls, and **checked integer addition inside a floating division operand**. The newly added `tests/Atto.Cil.Float.Guest.Host` bridge observes the generated CIL entry points and expects exit code 42 only when every condition is true; its C# code does **not** perform guest division.

`scripts/test-cil-guest.sh` now compiles and executes this positive fixture via CoreCLR and separately publishes and runs a real Linux NativeAOT host. It retains the previous four CIL-positive fixtures, including the independently added signed-minimum regression. Its **nineteen** negative inputs include three original integer-result division rejections, three new float-context/type rejections, and the one-below-minimum no-artifact rejection. For every negative, require the exact diagnostic, exit code 2, and **no output DLL**.

The source-local workflow `direct-cil-float-division-admission.yml` pins the exact PR commit, tree, and relevant compiler/host/fixture Git blobs and runs this actual guest matrix. A successful compiler build is not sufficient without real CoreCLR and NativeAOT execution. This gate is authored but **NOT_EXECUTED** until an authorized runner completes nonzero steps on the exact SHA.

## Explicit limitations / release blockers

This profile does not yet support float parameters, float literals, mixed arithmetic, return-type expected coercions other than this exact `int/int -> float` operation, `Integer.div_trunc`, decimal/high-precision division, guest Result/error emission, source imports, or general actor/tenant runtime execution. **Division by zero and the exact IEEE/error taxonomy for `int/int -> float` require a versioned spec/conformance decision** before broader promotion; the candidate CIL `div` currently follows the platform's floating-point arithmetic rather than defining a separate language exception ABI. Inexact signed-i64-to-binary64 rounding near the 2^53 boundary also requires shared Java/.NET expected-bit-pattern fixtures.

No SDK/compiler, GraalVM guest, CoreCLR runtime, or NativeAOT execution is claimed for this GitHub update. Static inspection, a workflow declaration, or GitHub `steps=[]` failures are **NOT_EXECUTED**, not PASS. Keep draft and `keep-as-draft` until the actual exact-head test run and independent reviewer checks.

## Nonduplication / adjacent dependencies

- [atto-spec #151](https://github.com/atto-lang/atto-spec/pull/151) and [#143](https://github.com/atto-lang/atto-spec/pull/143): canonical division and operator decisions; the overlapping operator chapters are reconciled at exact Git blob.
- [atto-source.java #66](https://github.com/atto-lang/atto-source.java/pull/66): separate checked i64 / floating-division Graal guest regression; currently **not independently executed**.
- [atto-source.net #120](https://github.com/atto-lang/atto-source.net/pull/120): shared arithmetic oracle only, not a compiled Result or division guest.
- The legacy `Ores.Net.Compiler.Core` shipping scalar/structured paths and the separate experimental Result/trap compiler [#117](https://github.com/atto-lang/atto-source.net/pull/117) are **not** modified here; parity still requires their own compiler-backed receipts.

No merges, permission weakening, private CI mirroring, or source-org branch protection changes.
