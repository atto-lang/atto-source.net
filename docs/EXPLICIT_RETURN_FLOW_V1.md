# AttoLang explicit return control-flow contract — .NET

Every non-`void` `fnc` or method must have an explicit `return value;`
on every reachable exit path. `return;` cannot satisfy a non-void result.
A `void` `fnc` may fall through or use `return;`, but may never return
a value. A **braceless expression lambda** is the sole value-return
exception: its expression is its value. A **braced lambda** is statement
bodied and requires an explicit return when non-void. A nested lambda's
return does not satisfy its containing function's return obligation.

The experimental structured .NET CFG compiler now represents void
returns with a distinct `CfgReturnVoid`, verified independently of
`CfgReturn(valueId)`. No synthetic i64 `0` is created for a void
function. The MethodDef has a real CIL `void` return signature and
a stack-empty `ret`; empty void methods have zero SSA definitions.
A nonvoid CFG with `CfgReturnVoid` and a void CFG with `CfgReturn`
are rejected, even if a future parser is bypassed. The original scalar
compiler remains unchanged.

Scope: v1 accepts **unused void helper declarations** in the bounded
structured CFG module and compiles their actual CIL MethodDef.
Void **calls** require separate effect-only call IR and remain fail
closed; this does not claim general void effects, callbacks, actor
closures or lambda parsing/lowering. Nonvoid `main(): int` remains
mandatory and must explicitly return. A braceless lambda remains a
language-spec exception that this backend currently does **not** admit.

Tests exercise source return coverage, all-branches explicit returns,
loop-only nonproof, void fallthrough/early returns, forged CFG
terminators and CIL metadata. Only compiled, executed guest tests
certify the supported subset. Full original-tree testing and independent
review remain required before merge.
