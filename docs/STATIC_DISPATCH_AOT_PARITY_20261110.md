# .NET NativeAOT static-dispatch parity audit (2026-11-10)

**Status:** real structured scalar-helper CIL emission already uses closed direct `MethodDef` calls, while full nominal class/trait/virtual dispatch remains **NOT_IMPLEMENTED**. This branch strengthens verification; it does not add production support for classes or claim fully executed native AOT tests.

Shared target contract: `oreslang-common-source/docs/STATIC_DISPATCH_AOT_V1.md` (PR #265).

## Existing narrow implementation

`OresStructuredCfgLowering.LowerModule` produces a verified helper-signature map and separate CFG methods. `CilEmitter.EmitLibrary` emits private static helper `MethodDef` tokens. The existing `StructuredStaticAbiTests` already compares every CFG call target with the actual CIL `Code.Call` target and verifies that the method belongs to the correct generated type with a matching, bounded ABI.

Unlike the general AttoLang language goals, this is only a statically selected scalar-helper subset. Nominal class construction, instance methods, virtual override tables, and trait witnesses are **not** supported by the production .NET structured compiler; the parser is expected to fail closed, not silently choose runtime lookup or fake CIL.

## This PR

- Factor a closed `Code.Call` validation check out of the existing CIL helper verifier.
- Reject `Calli`, `Callvirt`, `Ldftn`, `Ldvirtftn`, `Newobj`, `Jmp` and `Ldtoken` in the **scalar-helper-only** direct-call profile.
- Generate legitimate CIL from verified source, then deliberately corrupt the call opcode in memory and check that every forbidden opcode fails the verifier. This tests the real emitted assembly representation, **not** an arbitrary source string.
- Keep exact MethodDef owner/private/static/callee prefix and stack constraints; the regression cannot be satisfied by an external method with a matching arity or name.

## Remaining acceptance gates

1. Execute `dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release` against the exact PR head; record job attempts, nonzero steps and compiler SHA.
2. Compile and execute the supported scalar helpers through real `dotnet publish -p:PublishAot=true` NativeAOT, with source/codegen identities. The emitted-CIL interpreter alone cannot prove strict native guest execution.
3. Implement nominal receiver binding, finite trait witness closure, actor-generation routing and genuine virtual dispatch with a closed precompiled fallback before claiming those backend categories.
4. Run malicious CIL/CFG mutation controls, actual native sanitizer/ownership tests as applicable, and independently review before merge.

A zero-step source-org CI failure remains runner admission, **NOT_EXECUTED**, never evidence of static CIL correctness.
