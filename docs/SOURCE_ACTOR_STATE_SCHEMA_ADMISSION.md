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
