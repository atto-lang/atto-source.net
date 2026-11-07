# Stage-1 compiler-owned atto.core/identity import (.NET)

The source compiler now recognizes a deliberately narrow, pure stage-1
`atto.core/identity` module. Its canonical .atto bytes are embedded in the
compiler assembly by its project file. The typed module graph traverses the
compiler-owned pseudo-identity `@atto.core/identity.atto`, includes its
actual UTF-8 bytes in the source witness and graph SHA-256, and lowers
the public `passthrough(int)` function through the ordinary verified CIL
pipeline. The native compiler is never permitted to select an ambient
`--root/atto.core/identity.atto` file for this import.

The six canonical/historical import spellings alias one source unit; tests
compile and execute each, check a user-supplied shadow returning 99 remains
ignored, and reject unknown/unsupported Core/Alloc/Std/actor imports and
wrong module aliases. Explicit local ./... imports retain their existing
meaning.

**Scope:** this proves only a primitive Core identity function and typed
module provenance. The canonical `numeric.atto` file has richer `if/do/fi`
semantics and is not admitted by this stage-1 linker yet; no claims of
full Core/Alloc/Std parity or other backends' guest execution. The real
NativeAOT CI is still blocked until source-owned runners allocate.
