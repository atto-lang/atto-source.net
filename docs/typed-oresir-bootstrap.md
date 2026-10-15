# Typed OresIR — incremental bootstrap (stage 1)

**Status:** Draft implementation, unverified by .NET toolchain. **Stacked on [Native AOT PR #1](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/1).** Do not merge this branch into `main` before its dependency is merged or the PR is rebased.

## Intent

Introduce a small, **typed, verified** intermediate representation shared between parsing and future CIL lowering. The first stage covers only the currently accepted `i64` arithmetic expression subset. It is deliberately not a second language parser or an alternative Native AOT runtime.

## Representation and invariants

- A function has a named result type, ordered list of value-producing instructions and an explicit return value ID.
- Instructions are in SSA order with IDs `0..N-1`; each operand must refer to an **earlier** value definition. No implicit locals, forward references or duplicate IDs.
- `ConstantI64` carries exactly one `long` literal and no inputs.
- `CheckedAdd`, `CheckedSubtract`, `CheckedMultiply`, `CheckedDivide` have two previously defined `i64` operands and no literal payload.
- Unknown opcodes, mismatched result types, null instructions, invalid function identifiers, malformed arities, unsupported return types, invalid returns and excessive budgets fail before downstream lowering.
- Verification snapshots the incoming IR into a readonly instruction collection; mutation of the input list **after verification** cannot change the approved program.
- AST lowering uses explicit iterative postorder traversal. A deeply left-associated source expression cannot consume the CLR call stack.
- The current supported AST and IR have **no** branches, phi values, modules, generics, effects, ownership, actors, asynchronous suspension or borrow semantics. Do not infer that those features are implemented.

## Validation

`verification/Ores.Net.Tests` exercises positive lowering, arithmetic precedence, negative SSA/type cases, input snapshot immutability, 2,048-term expressions and a managed test-only reference interpreter for checked integer arithmetic, divide-by-zero and signed overflow including `long.MinValue / -1`.

The reference evaluator is **test-only**. The production Native AOT compiler does not load or execute generated guest assemblies, generate code at runtime, or invoke a runtime JIT.

## Integration path

1. Recover CI execution (independent unresolved issue) and ensure all regression tests pass on .NET 10.
2. Extend the typed IR to parameters, locals, function calls, explicit effects and source span diagnostics; add dominance/type constraints before introducing control-flow edges.
3. Add basic blocks with explicit branch terminators, dominance checking, phi nodes or block parameters, and verified CFGs.
4. Add linear ownership/effect states and explicit actor operation contracts; reject borrows crossing suspend points until proof obligations are defined.
5. Replace bootstrap direct-AST CIL lowering only after differential conformance demonstrates equivalent behavior for the existing arithmetic subset and negative cases.
6. Keep `System.Reflection.Metadata` as build-time CIL backend; publish native artifacts with `dotnet publish -p:PublishAot=true` and continue to prohibit runtime JIT and dynamic assembly loads.

## Verification gate

**No green tests are claimed.** The Actions infrastructure has repeatedly failed before reporting executed steps, and the current working environment lacks a .NET SDK. Keep the stacked PR in draft until compiler, IR and Native AOT tests actually pass.
