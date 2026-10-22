# .NET Native AOT actor-owned heterogeneous layout

Status: **draft runtime-host substrate**, not source-compiled Oreslang or verified Native AOT.

`ActorMixedShard` adds `u32`, `f64`, `bool`, and `ref32` typed fields in an actor-owned **unmanaged** `ActorArena` persistent allocation. There is no per-actor CLR managed GC heap. Ordinary C# metadata (field names, checked locators and descriptors) remains managed; Oreslang guest payload bytes live entirely in the native actor arena. `ref32` is an unsigned encoded reference **word**, not an actual pointer or permission to dereference.

Layout kinds: AoS (24-byte mixed record stride), SoA (separate aligned field segments), AoSoA (checked power-of-two tiles with masked non-live tail), grouped SoAoS (independent arrays of compact subrecords). The shared golden fixture is `id:u32, x:f64, flag:bool, next:ref32`; three rows except AoSoA tile-width four with five rows. Expected physical bytes AoS 72, SoA 56 (51 bytes of payload plus 5 bytes aligning segment bases), AoSoA 144, SoAoS 72 with groups `[[id,flag],[x,next]]`.

Metadata and 16-MiB physical quota are validated before `ActorArena.AllocatePersistent`; all typed methods address the private allocation through turn/owner/generation checked `ActorArena.Read/Write`. Writes are small stackalloc-encoded little-endian primitives, without per-cell managed object allocation. The shard can be deterministically released, scrubbing and reclaiming native bytes only under a valid actor turn and when the arena has no conflicting leases.

`BorrowRead(turn)` now delegates to `ActorArena.BorrowRead` for a conservative **whole-allocation** immutable lifetime loan. It permits owner-turn reads, blocks all writes and release while held, and can survive a scheduler turn boundary without granting any out-of-turn dereference authority. New tests verify that an active loan prevents mutation, prohibits reclamation, survives turn exit/reentry, then restores writes and deterministic cleanup when released. Field-sensitive or suspension-aware compiler lowering is not yet implemented.

`ActorMixedShardSelfTest` checks golden physical byte lengths, uint32 maximum, signed zero, NaN, typed rejection, group membership/stride, invalid layouts/overflow, foreign actor-turn rejection, deterministic release and no partial allocation on bad inputs.

**Not yet demonstrated:** built .NET artifacts or actual dotnet test execution, generated CIL/Native AOT Oreslang source admission, all-layout ECS source compilation, byte-sensitive borrow checker, generational row handles, untrusted process sandboxing or exact-head release certification. This draft is stacked on .NET four-layout PR #108. Toolchain-independent review is useful, but all executable claims wait for stepful dotnet/Native AOT CI.
