# AttoLang .NET NativeAOT actor-local 32-bit guest references

Status: **host runtime implementation candidate; exact-head tests NOT_EXECUTED, compiler-produced guest lowering NOT_IMPLEMENTED, release NOT_CERTIFIED**.

## Design

`ActorRuntime` already allocates one `ActorArena` per actor. The new
`ActorGuestHeap32` adds a **fixed-width 32-bit guest reference table** over
that existing arena, not a second native allocation or a custom CLR GC heap.

```
guest word:  [ generation: 16 bits ][ slot + 1: 16 bits ]
             0 = null
actor context + slot -> ActorArenaHandle -> actor-local native slab offset
```

An AttoLang object-reference field stores exactly four bytes in little-endian
form via `WriteGuestReference32` / `ReadGuestReference32`. The native slab
uses the existing `NativeMemory.AlignedAlloc`/`AlignedFree` allocator and
actor identity, incarnation, physical-turn, budget and loan checks. No raw
native pointer or actor authority is exposed to guest code.

**These are compressed indirect references, not literal 32-bit offsets.**
Indirection is intentional: slot generations reject stale references without
depending on whether the bump allocator reuses a physical offset. The logical
format is not yet a frozen wire ABI. A future shared backend object ABI must
choose a common representation explicitly, rather than assuming an arbitrary
raw offset is a safe object capability.

There can be at most 65,535 guest slots per actor, further capped by
`ActorMaxAllocations` (shared with other arena persistent allocations).
A slot is permanently retired after generation 65,535 rather than wrapping
to 1. Exhaustion is explicit and cannot revive a stale reference. The
32-bit value is **only meaningful with its owning actor's execution context**;
two separate actors can have equal numeric reference words that resolve to
different native bytes.

## APIs (host runtime)

`ActorContext.AllocateGuestObject32(bytes, alignment)` allocates a
persistent native object and returns a 32-bit reference.

`ReadGuestObject32` / `WriteGuestObject32` perform checked, turn-authorized,
allocation-bounded byte access. `ReadGuestReference32` /
`WriteGuestReference32` validate typed local reference fields. A nonzero
stale, unknown or malformed reference fails closed. `DropGuestObject32`
drops the underlying persistent arena allocation before recycling the slot.
Existing arena loans still block mutation/drop. Retained contexts cannot
create or dereference objects after the physical actor turn.

**Important:** Raw `uint` values are not cryptographically authenticated
capabilities. Never pass guest references through actor messages, external FFI,
network serialization, a host global, a capability grant or another actor's
context. A foreign reference word could numerically coincide with a local
reference. Enforce this boundary in typed compiler lowering and through
explicit transport rejection. Untrusted actors still need a separately
certified isolate/process protection boundary.

## What this does not yet prove

- No source-level `define actor` object/class, string, tuple, map, array or
  record lowering into this table.
- No source ownership/borrow-checker path-to-heap proof or typed field map.
- No zero managed allocations in the production `ActorArena` path: each
  `AllocatePersistent` currently creates a managed capability object.
  Existing `ActorNativeArena` has a separate primitive-handle low-allocation
  path, but this patch does **not** silently substitute that allocator.
- No lowering of compiler-generated async suspension, unwinding, destructor
  schedules, CPU/GPU layouts, or an AOT full-source actor guest.
- No promise of cross-actor authorization based on the 32-bit number alone;
  no source-level cross-backend parity or protected untrusted sandbox claim.

## Verification gates

The existing `verification/Ores.Net.Tests` runner now calls
`ActorGuestHeap32SelfTest` and covers two actual host actor cells, multiple
physical turns, independent private values at equal local reference words,
malformed/forged/stale/null references, bounds and double-drop denial,
generation wrap exhaustion, retained-context rejection and native-budget
refund after STOP.

The existing Linux x64 / Windows x64 NativeAOT actor-memory probe now exercises
32-bit guest refs in all four actor cells and emits a distinct exact-head
`PASS actor-ref32-native-probe` host receipt. The workflow requires it in
addition to all previous actor-memory and logical-stack receipts.

Execute on an authorized runner from this PR's **exact head**:

```sh
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
dotnet publish verification/Ores.Net.ActorMemoryProbe/Ores.Net.ActorMemoryProbe.csproj \
  -c Release -r linux-x64 -p:PublishAot=true -p:SelfContained=true
```

Also run the Windows x64 NativeAOT probe, inspect non-skipped GitHub job
steps and compare checkout SHA, tree, produced artifact identity and probe
output before claiming executed evidence. A green workflow with no runner,
no steps, skipped tests or a stale SHA is **NOT_EXECUTED / NOT_CERTIFIED**.

Next compiler gate: lower a real typed actor with two heap-allocated guest
objects and a four-byte child-reference field, execute it after physical turn
migration, then prove forged cross-actor references and stale references
cannot escape typing/transport/borrow rules. Run the same canonical fixture
on Java/GraalVM and LLVM before any parity claim.
