# .NET Result<int,Error> parsing / source compiler admission

The direct-CIL Atto compiler currently handles scalar-only int expressions,
not generic tagged Result/Option, match, postfix ? or ordinary throw/trap.
Four new .atto negative fixtures are checked for compiler rejection with a
deterministic E_LEX/E_PARSE diagnostic and NO OUTPUT ARTIFACT.

A separate host-only C# reference in ResultIntParseV1.cs implements tagged
Ok/Err, strict signed-i64 decimal grammar, 1,024-byte admission bound, and
immutable Error identity scoped to a trusted tenant. It is tested on CLR and
Linux NativeAOT. This does not compile the shared Atto parse_id example, and
must not be counted as guest conformance. No C# host parser may be substituted
for generated Atto guest logic. Portable Result/error ABI and type checking
must be implemented in the CIL emitter before claiming parity.

Tracked by atto-spec #124, atto-platform-conformance #68, and
atto-compilers-parity-testing #20. Keep unsupported capabilities fail-closed.
