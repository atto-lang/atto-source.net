# Verified F64 inverted-class layout metadata — .NET Native AOT v1

**State: draft, host-runtime/compiler-metadata only, NOT_EXECUTED.** This
implements an immutable, checked scalar storage descriptor that can bind the
existing native actor-owned `ActorShardColumns` to an independently
validated field/schema layout. It **does not** implement `.atto` parsing,
typed guest HIR/MIR, CIL emission, borrow checking, source method dispatch,
scheduler authority, or real inverted-class language semantics.

## What is implemented

`NativeInvertedF64LayoutVerifierV1.Verify` accepts a static class name,
rows, ordered field identifiers and exactly one physical layout
(`Aos`, `Soa`, `Aosoa`). The present runtime implementation holds only
homogeneous **64-bit floating-point** scalar columns. Do not reinterpret
this as checked `int` or heterogeneous-class support; those will require
independent typed layout lowering.

- Nominal class and field identifiers must be bounded ASCII identifiers.
  The field list is immutable after validation, with duplicate names denied.
- One to 32 fields, positive rows, and at most 16 MiB of **physical** actor
  shard storage; padding is included in the byte budget.
- AoSoA lane sizes are powers of two up to 1,024; AoS/SoA require one lane.
- `ByteOffset(row,field)` is checked, excludes padded logical rows and
  guarantees that every 8-byte value remains in the verified native backing.
- `DescriptorIdentity` is descriptive static metadata, **not** an
  authenticated identity, a binary cryptographic hash, or an actor ticket.
- The plan is a frozen snapshot. Changing the caller's field collection
  cannot mutate it or reinterpret an already allocated shard.
- `AllocateTrustedHostShard(arena,turn)` calls the existing actor arena
  runtime; its memory authority comes exclusively from the arena's checked
  actor turn, never the descriptor or a numeric actor ID.

`SourceGuestEmissionCertified` is permanently false. These types are
compiler-side milestones; the shipping .NET scalar frontend must continue
to reject `define inverted class` until a source-backed lowered execution
path is implemented and tested.

## Physical F64 geometry

For `rows = R`, `fields = F`, `lanes = L`, logical row `r` and
field `f`:

- AoS byte index: `8 * (r*F + f)`.
- SoA byte index: `8 * (f*R + r)`.
- AoSoA byte index: `8 * (((r/L)*F + f)*L + r%L)`.
- AoSoA physical rows: `ceil(R/L)*L`; physical bytes:
  `8 * physicalRows * F`.

These expressions deliberately match the existing .NET
`ActorShardColumns` layout contract. They are not interchangeable with
heterogeneous byte-stride layouts in Java/LLVM, which have independent
typed/alignment requirements.

## Source admission remains deliberately closed

Both the shipping scalar parser and the experimental structured parser
recognize declaration-context `define inverted class` / `define pub inverted
class` and reject with the same capability-specific diagnostic and UTF-16
source offset. Comments containing those words stay inert. Existing
`inverted struct` denial is preserved. Verified static metadata cannot
authorize source compilation into CIL before ownership checking and actual
guest lowering exist.

## Testing

The existing `verification/Ores.Net.Tests` runner now invokes
`NativeInvertedF64LayoutPlanV1Tests.Run()`. It covers all three layouts,
exhaustive small-shape non-overlap and bounds, immutable snapshots,
non-authenticating metadata IDs, real native actor-owned shard integration,
identical numerical results for a dense update, exact physical quota
reclamation, invalid identifiers, duplicates, malformed widths and
padded-capacity exhaustion.

```sh
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
```

An additional dependency-free **Rust** geometry oracle compares exact AoS,
SoA and AoSoA F64 offsets, exercises all small 1–65-row layouts for
non-aliasing, checks tail padding, and rejects malformed or oversized
regions. It is not a substitute for compiled .NET or native guest tests.

```sh
rustc --edition=2021 --test tests/native_inverted_f64_layout_v1.rs \
  -o /tmp/native_inverted_f64_layout_v1
/tmp/native_inverted_f64_layout_v1
```

**No successful SDK 10 or Native AOT run has been observed for this PR.**
Host tests, even when executed, are only proof of the host substrate.
Before promoting language parity, require a real `.atto` program and
negative fixtures: type checking, source-to-CIL lowering, actor
capabilities, loan conflicts, generations, zero-copy safe transfers and
exact native outputs. Run executable guest programs on .NET Native AOT
Linux/Windows, Java/GraalVM and LLVM with independently checked source
Git blobs and compiler heads.

Native actor-owned F64 bytes are unmanaged, but CLR host metadata,
scheduler/control and test objects still allocate. A whole-process
zero-GC claim is explicitly unsupported.
