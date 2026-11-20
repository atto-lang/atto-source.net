# .NET NativeAOT source IDs v1

**Implementation status:** internal C# source-identity metadata admission and registered self-test only. No claim of Atto guest compiler probe lowering, native source-site runtime spans, independent .NET JIT compilation, reflection-based code generation, or five-backend parity.

The NativeAOT backend independently parses `rid_` and `sid_` values into two unsigned 64-bit lanes, and validates immutable site descriptors for precompiled artifacts. The semantics match the source-ID interchange contract without sharing Rust/Cranelift, LLVM/C++ or Java/Truffle runtime/compiler code.

Run the repository verification runner as usual, including `NativeSourceIdentityV1SelfTest.Run()`. Expected scoped marker:

~~~text
PASS NET_NATIVE_SOURCE_ID_HOST_METADATA_V1:...
~~~

The marker represents **host metadata parsing and table validation**, even if that test is executed in a published Native AOT executable. It is not a guest compiler/JIT/span proof.

## Independent .NET backend next gates

1. Bind source IDs to this repository's own validated `.atto` CIL emitter, source overload resolution and compiler-known callsite locations; reject duplicated/copy-pasted IDs before output.
2. Pair generated static descriptors to the exact Native AOT artifact/build ID; avoid runtime reflection/dynamic IL generation and unbounded runtime logging allocations.
3. Emit truly precompiled guest function/boundary probes with independent CIL/NativeAOT lowering and logical task/actor trace context.
4. Prove same-image runtime recording OFF→ON→OFF→ON for *already compiled* sites. Do not promote host dispatch/policy toggles to guest JIT evidence.
5. Dynamic module/version admission, if implemented, must use independently signed/verified precompiled artifacts, immutable code-generation fences and compatible ownership/continuation ABI.
6. Assert runtime `trace_id`/`span_id` fields are distinct from static `routine_id`/`site_id`, with OTel-compatible propagation/export and privacy constraints.

Related: [AttoLang source identity #62](https://github.com/ores-truffle-oreslang/attolang-language-spec.md/pull/62), [independent compiler contract #287](https://github.com/ores-truffle-oreslang/oreslang-common-source/pull/287), [.NET native trace policy #512](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/512).
