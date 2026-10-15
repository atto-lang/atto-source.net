# Typed OresIR control-flow verifier — bootstrap slice

**Status: draft, not compiled or executed.** This implementation is stacked on
[typed OresIR PR #2](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/2),
which in turn depends on [Native AOT PR #1](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/1).
Do not merge ahead of the dependencies.

## Scope

`ControlFlowIr.cs` adds a separate, strictly verified, typed control-flow
representation. The existing `oresnet` source parser and production CIL emitter
remain unchanged by this draft; they still accept only the arithmetic subset.

- Explicit, named basic blocks and terminators (return, jump, conditional branch).
  No implicit fallthrough or unchecked control-flow edges.
- Typed `i64` and `bool` values with unique, dense SSA IDs. Instruction
  operands and return values must be defined, visible and type-correct.
- Block parameters represent values supplied on incoming edges (phi semantics),
  including loop-carried values. Each edge must supply exactly the required
  argument count and types.
- Branch conditions must be `bool`. Function results remain `i64` in this
  bootstrap. Invalid opcodes, extra literal payloads, missing operands and
  unsupported value types are rejected.
- Every block must be reachable from the entry. The entry takes no block
  arguments. Target names and function names use bounded ASCII identifiers.
- A forward iterative dominator fixed point handles loops and diamonds. SSA
  definitions must precede local uses or dominate uses in other blocks.
  Values from a sibling predecessor are rejected at the join unless passed
  through a typed block parameter.
- The verifier snapshots instruction, parameter and edge-argument arrays,
  preventing mutations of untrusted input collections from changing the
  verified CFG.
- Resource limits: 128 blocks, 16,384 SSA definitions. No recursive traversal,
  code execution or runtime assembly loading during verification.

## Executable test fixtures (managed developer tests only)

`CfgVerifierTests` covers a loop incrementing a block argument from 0 to 5,
a diamond join, missing targets, incorrect edge arity/type, undefined and
non-dominating definitions, invalid condition types, unreachable blocks,
duplicate blocks, malformed operation payloads and snapshot immutability.
`CfgReferenceEvaluator` is a test-only bounded interpreter and explicitly
raises errors for arithmetic overflow and division by zero.

**Important:** Executing the reference interpreter does **not** execute CIL or
a Native AOT artifact. This PR does not include CIL branch encoding or
CFG-to-native integration. Because GitHub Actions jobs still fail without
exposing executed steps, all new tests are **committed but unverified**.

## Further hardening / next milestone

1. Verify compilation with .NET 10 and execute every regression, including
   the parent OresIR-to-CIL differential suite and the CFG test fixtures.
2. Add explicit source-level functions, typed parameters, local scopes and
   control-flow constructs to the frontend. Lower them to checked CFG IR.
3. Implement CFG-to-CIL: assign slots, encode branches with a control-flow
   builder and label handles, verify stack height at each block entry, and
   preserve checked arithmetic traps and observable effects.
4. Add dominance property tests and bounded exhaustive exploration of small
   CFGs, including backedges, conflicting predecessor values, and no-return
   loops; then implement live-range-aware slot reuse without dropping traps.
5. Add effect and ownership analysis (move/borrow/share, cancellation,
   suspension). Reject unsupported operations before lowering or publishing.
6. Preserve strict .NET Native AOT with no runtime JIT, Reflection.Emit,
   dynamic assembly loading or LLVM dependency.

A green Windows x64 Native AOT compile and execution remains a non-negotiable
merge gate for the end-to-end compiler pipeline.
