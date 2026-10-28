# .NET typed scalar binding-kind parity (experimental)

Reference: [Java LANGUAGE.md / Bindings](https://github.com/ores-truffle-oreslang/oreslang-source.java/blob/main/docs/LANGUAGE.md). Java distinguishes `const` (compile-time), `val` (runtime single assignment) and `let` (mutable).

This change admits all three **only in `OresStructuredParser`**, with an explicit `int` or `bool` type:

```ores
fnc main():int {
    const int limit = 6 * 7;
    val int snapshot = limit;
    let int current = snapshot;
    current = current + 1;
    return current;
}
```

- `const` initializers must be scalar, pure, compile-time evaluable expressions; checked i64 overflow is rejected during parsing. Initializers and references to earlier `const` values are canonicalized to typed literals before CFG/CIL lowering.
- `val` may read runtime locals but cannot be assigned again.
- `let` preserves mutable-local behavior.
- Duplicate declarations, self references and attempts to reassign `val`/`const` are rejected, including from nested blocks.
- The source subset is intentionally narrower than Java's inference, objects, destructuring and non-scalar constants. Unsupported forms continue to fail closed.

**Non-claim:** this is a typed structured-parser and AST-contract improvement, not a shipping scalar `oresnet check/compile` feature. Structured CFG/CIL execution is under review in #155 and #185. Do not claim generated CIL or Native AOT parity until this branch and those dependencies receive exact-head executed tests and independent review. Source-org Actions currently fail before runner allocation; zero-step runs are not evidence. Do not export private source to another organization without approval.
