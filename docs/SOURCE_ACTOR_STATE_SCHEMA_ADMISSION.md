# Source actor sealed-state schema admission

Status: **parser metadata boundary only; executable source actor lowering is unavailable**.

The scalar .NET Native AOT frontend now recognizes the canonical isolated-actor
shape far enough to validate the sealed state schema:

```ores
define isolated actor Counter as
    state {
        count: int = 0;
        enabled: bool = true;
    }

    pub increment(): int {
        return self.handle_state(critical |s| -> s.count);
    }
end
```

The admitted schema is deliberately narrow and bounded:

- isolated actors must declare `state { ... }` before methods;
- 1-16 fields;
- unique identifier names;
- `int` and `bool` only in this metadata slice;
- signed-i64 literal defaults are range checked;
- boolean defaults are exactly `true` or `false`;
- legacy non-isolated actor declarations remain rejected.

After schema validation the compiler always fails closed with the explicit
sealed-`handle_state` / generation-fenced logical-turn diagnostic. No state
field becomes an ordinary CLR object field, no OresIR/CIL actor layout is
emitted, and no actor body is executed.

This branch is stacked on PR #259, which separately rejects host `async void`
actor handlers that could escape physical turn admission. The runtime
logical-turn substrate is developed independently in PR #264 on top of #243.
Future integration must join these boundaries: source suspension releases only
the physical carrier, keeps the same actor-wide logical user turn active,
retires any live state-backed loan before suspension, and resumes using a
generation-fenced continuation before reacquiring state access.

Do not infer source actor parity from parser admission. Managed tests plus real
Linux/Windows NativeAOT execution of the integrated source/runtime lowering are
release gates.


## Typed immutable schema metadata (October 30, 2026)

The compiler now exposes `OresParser.InspectActorStateSchema(source)`
for **metadata-only** inspection of a single canonical isolated actor
declaration in a source string. The method reuses the normal parser's source
byte budget, UTF-16 validation, lexical treatment of comments/strings and
bounded declaration checks. It produces an `ActorStateSchemaMetadata`
with the nominal actor name, actor/state source offsets and immutable,
ordered `ActorStateFieldMetadata` descriptors.

Each field has a zero-based index, source name, source offset,
`ActorStateFieldKind.Int64` or `Boolean`, and the typed literal default.
The collection is backed by a defensive read-only copy. Multiple actor
declarations are rejected rather than silently selecting one. Signed-i64 parsing
includes `long.MinValue`, but rejects overflow; scalar field count remains
1–16. No field reflection, dynamic property lookup, mutable descriptor
array or guest-write capability is returned. This is a schema inspection
surface, **not a whole-program actor type checker**.

The normal `OresParser.Parse(source)` still rejects source actors with
the existing generation-fenced `handle_state` lowering diagnostic.
The metadata API supplies **no** `ActorTurnTicket`, `ActorStateLoan`,
authenticated scheduler identity or executable CIL/IR; an untrusted caller
cannot use metadata inspection as actor execution authorization.

The independent runtime draft #264 supplies host-owned scalar cells and
atomic loan-checked state operations. Integrating these branches will require
compiler-generated layout initialization with field kinds/defaults,
the lexical `critical` callback and effect checker, and scheduler-issued
turn/loan tokens bound to the generated state operation. Do not emit a
`ValidateActorStateLoan` call followed by an unguarded CLR field access.

`ActorStateSchemaAdmissionTests.Run()` is wired into the existing
`verification/Ores.Net.Tests/Program.cs` executable and checks ordered
fields, source offsets, immutable descriptors, min/max signed defaults,
typed booleans, 16-field maximum, invalid/duplicate fields and continued
fail-closed actor execution. This is test **source**, not an executed
NativeAOT result. Linux/Windows exact-head build, guest behavior and
independent approval remain mandatory before merging.
