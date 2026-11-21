# .NET AttoLang actor heap V1: narrow source-to-NativeAOT lowering

Status: **source-authored compiler implementation; NOT_EXECUTED / NOT_CERTIFIED**.
This change stacks on runtime draft PR #437, which supplies the checked
`ActorGuestHeap32`, `ActorGuestLayout32`, and `ActorContext` native-heap API.

## Why this exists

The shipping .NET scalar and structured compilers intentionally reject
general `define class` and `define actor`. Do not remove that admission
gate to pass a fixture: the general nominal type, borrow checker,
exceptions, actor dispatch, and CIL lowering are not yet available.

`ActorHeapV1Compiler` introduces a separate, explicitly opt-in
`emit-actor-heap-v1` command. It takes **actual .atto source**, tokenizes
it (including nested comment handling), checks a small typed object-graph
grammar, and emits deterministic C# source. The normal .NET 10 NativeAOT
compiler then statically compiles that source into a native executable.
There is **no runtime parsing, IL loading, Reflection.Emit, or managed
object emulation for guest Node allocations**.

### Accepted source milestone

The source fixture at `contracts/actor-guest-heap/v1/node_graph.atto`
contains one nominal class with exactly:

- One `int` field (mapped to a checked 64-bit native integer for this
  existing .NET scalar subset)
- One nullable self-reference field (one generation-fenced 32-bit guest
  reference; `none` is zero)

It also contains one actor whose `receive(int message)` body has two
`let` bindings constructing this class, followed by an `assert` of
the second instance's linked first-instance value. This exact *grammar*
is accepted, not a hard-coded source-string fixture. Signed 64-bit
constants, class/actor names and local identifiers are parsed and checked.
A different literal changes emitted guest behavior, and a wrong expected
literal fails at runtime rather than being normalized away.

No other class layout, method, constructor, actor body statement, dynamic
dispatch, async operation, FFI, message reference transfer, or impure
operation is admitted. Unsupported text fails with an
`E_ACTOR_HEAP_V1_*` compiler diagnostic before any emitted file exists.

### Actor-bound reference and source-identity hardening

Generated `.atto` code **never** holds a naked `uint32` actor-object
capability. Every compiler-local object variable and dereferenced link is
an `ActorGuestOwnedRef32` carrying the runtime-minted arena identity.
Compiler output calls `AllocateOwnedTypedGuestObject32`,
`ReadOwnedGuestObject32`, `WriteOwnedGuestObject32`,
`ReadOwnedGuestReference32`, `WriteOwnedGuestReference32` and
`DropOwnedGuestObject32`. The ABI retains a 4-byte native Ref32 field,
but accessing that value through a sibling actor's context fails before
native metadata access, including on a coincidentally equal reference word.
The unbound raw `uint32` host API remains available solely for trusted
legacy callers; it is not a public actor-object authority.

The optional `emit-actor-heap-v1` CLI now rejects a UTF-8 BOM rather
than silently removing it. It uses a bounded, double-read source snapshot
with byte comparison and last-write-time checks, so provenance refers to
the admitted bytes. A negative workflow test constructs a BOM-prefixed
copy of the exact fixture and requires compiler rejection **without
emitting a file**.

### Checked lowering

For the accepted graph, generated `ActorHeapV1.g.cs` contains:

1. A static, immutable `ActorGuestLayout32` with type ID 1 and 16 bytes:
   `I64` at offset 0, nullable `Ref32` at offset 8.
2. `receive(ActorContext context, int message)` which allocates *two*
   typed guest objects through the actor's existing private native arena.
3. Little-endian checked scalar writes and exact `Ref32` link creation.
4. A typed dereference and signed-integer equality assertion matching
   the source. The checked runtime rejects stale, forged and wrong-type
   object references.
5. Nested `finally` cleanup which zeros the link and attempts the
   second object's release, then always attempts the first object's release
   even if reference clearing or a prior drop throws. Source assertion
   failures therefore cannot simply skip the owned-root cleanup paths.
   A fatal native allocation/arena fault still requires dedicated execution
   evidence; source-only coverage does not certify successful reclamation.

The generated file lives at
`verification/Ores.Net.ActorGuestV1Probe/ActorHeapV1.g.cs`,
is gitignored, and is **not** checked in as a hand-authored C# proxy.
The probe's project fails closed when the generated source is absent.

## How to verify (authorized exact-head runner)

From the compiler PR head (stacked on #437):

```bash
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release

dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj \
  -c Release -- emit-actor-heap-v1 \
  contracts/actor-guest-heap/v1/node_graph.atto \
  --out verification/Ores.Net.ActorGuestV1Probe/ActorHeapV1.g.cs

dotnet publish verification/Ores.Net.ActorGuestV1Probe/Ores.Net.ActorGuestV1Probe.csproj \
  -c Release -r linux-x64 -p:PublishAot=true -p:SelfContained=true \
  -o /tmp/atto-actor-heap-v1
/tmp/atto-actor-heap-v1/Ores.Net.ActorGuestV1Probe
```

Do the corresponding `win-x64` publish and execute on Windows.

The exact-head workflow additionally **derives a negative fixture from the
pinned `.atto` source** by changing only the assertion expectation from
`42` to `43`. It records the mutated source and emitted C# digests, forces
a fresh NativeAOT rebuild, and runs the resulting binary with
`--expect-assert-failure`. Only a real generated
`E_ACTOR_HEAP_V1_ASSERT` fault qualifies: the host probe rethrows it so the
runtime executes the actor-fault retirement path, then requires one fault,
zero successful dispatches, no live actors, and zero native bytes reserved.
This is **one narrow assertion/cleanup path**, not a proof of all
`panic`/`raise`/`throw` semantics. Both Linux and Windows must execute
positive **and** negative native binaries on the same exact source head.

The compiler branch includes the latest runtime parent via a conflict-free,
two-parent Git merge. All four intersecting runtime file blobs were verified
identical before the merge. Neither draft is merged into `main`, and a
source-only commit does not demonstrate that the new execution gates passed.

The `actor-heap-v1-generated-nativeaot.yml` workflow checks the exact
source SHA and tree, verifies parser/emitter negative corpus, emits source
*from the checked .atto fixture*, fingerprints the source/generated C#
and binary bytes, and requires the NativeAOT executable's pass receipt.
The probe executes **six real actor turns in two independent actors**,
checks for zero unhandled actor faults, and proves both native slabs are
released on STOP.

The emitted C# is the *compiler-produced guest code* under test, but the
actor scheduler/runner is still a host-provided wrapper. This is a
**narrow, generated-guest milestone**, not general AttoLang actor
lowering, compiler-generated supervisor orchestration, a whole-actor
zero-GC guarantee, or five-backend parity.

## Security/acceptance gates

- Keep shipping `check`, `compile`, `check-structured` and
  `compile-structured` fail-closed for unsupported nominal declarations.
- Do not infer native execution from static source tests, green check
  metadata, cached artifacts, or zero-step Actions failures.
- Do not mirror private source to another GitHub organization absent
  active source-repo approvals, pinned snapshot IDs and independent review.
- Require stepful Linux and Windows NativeAOT execution receipts bound to
  the exact compiler PR head, fixture Git blob SHA, generated source
  digest and native binary digest before certification.
- Subsequent milestones: non-template typed AST/CFG nominal allocations,
  actor-state ownership, borrow checking, generic layouts, suspension
  into actor-owned logical frames, raise/panic/throw cleanup, and
  multi-backend conformance.
