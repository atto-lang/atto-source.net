# Value-boundary audit — 2026-10-15

The Java reference now supports `if expr as let value when predicate { ... } fi`
and the equivalent while head. It supports finite tuple indexing, aggregate
returns/destructuring and `f(...tuple)` argument expansion. Record/struct fields
are named, with order-independent structural compatibility; initializer
expressions evaluate in source order. No key-iteration contract follows from
host map storage. Ordinary maps should have no ordering promise; an ordered
map must be an explicit API. Pair/Entry are not implicit aggregate conversions.

This backend does not lower the general source surfaces exercised by
`value-boundary-rejections.json`. The corpus covers tuple/array indexing,
record and tuple destructuring, tuple spread, callable `.call`, templates,
async/await, suspend effects, inline if/while bindings, assumptions about map
key order, and nonboolean conditions. Rejection is an admission guarantee,
not language parity. Arithmetic is a positive control, so a broken compiler
cannot pass merely by rejecting every program.

Java single/double quoted strings are literal; `${value}` is not expanded.
Template interpolation and callable `.call` remain unimplemented there too;
use typed tuple spread for fixed positional arguments. Do not emulate missing
features through string substitution, host truthiness, argument truncation or
unverified synchronous execution. Completed futures must eventually resume
through an owning scheduler turn, including synchronous foreign callbacks,
failure and cancellation; source async support requires that machinery.

Run `dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release`.
Both the shipping arithmetic parser and experimental structured parser must
reject every corpus entry. This is a managed development test; it does not
prove Windows Native AOT application execution. The structured parser's
ordinary typed comparisons/if/while support does not include inline bindings.
