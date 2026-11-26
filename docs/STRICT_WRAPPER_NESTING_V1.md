# Strict Future / Option nesting v1

**Decision date:** 2026-10-27. **Status:** normative source semantics; .NET source lowering remains fail-closed for nested wrappers.

Nested wrappers are not aliases for their inner wrapper. `Future<Future<T>>` remains `Future<Future<T>>`, and `Option<Option<T>>` remains `Option<Option<T>>`. Host `Task` behavior, CLR helpers, or runtime convenience APIs must not change the Oreslang type.

Ordinary `await` removes exactly one `Future` layer. Ordinary `unwrap` removes exactly one `Option` layer. `flatten` is explicit and removes exactly one statically proven layer. There is no recursive implicit adoption or recursive auto-flattening.

The current scalar .NET bootstrap cannot lower general source `Future<T>` or `Option<T>`. Nested forms therefore fail closed with semantic diagnostics before CIL emission. This is intentionally stronger than accepting the type and silently erasing a layer, and stronger than failing incidentally on generic punctuation.

This contract supersedes the older recursive-Future-adoption proposal carried by draft PR #50. That host-only helper work is not language semantics and must not be used as parity evidence.

Executable parity requires a future typed source implementation to prove:

- one-layer `await` and `unwrap`;
- explicit one-layer `flatten`;
- `None` distinct from `Some(None)`;
- inner Future failure/cancellation preserved without implicit producer cancellation;
- actor continuations resume through the owning actor lane without reentrant mutable state;
- exact-head NativeAOT guest execution, not host-only helper tests.
