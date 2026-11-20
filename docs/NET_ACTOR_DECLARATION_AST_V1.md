# .NET actor declaration AST v1 — parsed metadata only

**Status: source-authored; managed tests not yet executed.** This change opens
the first compiler-owned representation for AttoLang `define actor` source.
It does not enable actor declarations in the structured scalar CFG/CIL
compiler and does not make actor handlers executable.

## Accepted declaration slice

```atto
pub define actor Counter as
    receive(self, int amount):void {
        // Retained as an exact opaque source span in AST v1.
    }
    reset():void { }
end
```

- Top-level `define actor Name as ... end`; optional leading `pub`.
- One or more named handler declarations with explicit `void` result.
- Optional leading `self` is erased from value-parameter slots.
- Only data-only `int` (i64) and `bool` message parameters are admitted in
  this first typed-signature slice.
- Handler body braces are balanced and the original body source span,
  offset and length are retained exactly. Nested braces, line comments,
  nested block comments and string literals are lexed separately so their
  contents cannot close a handler or create new declarations.
- Duplicate actor names, duplicate handler name/value-arity signatures,
  duplicate parameters, invalid receiver position, empty actor declarations,
  malformed body spans and unsupported punctuation are rejected.
- `await`, `suspends` and `critical` are rejected inside the opaque body
  until effect verification and continuation scheduling exist. Nested actor
  declarations are rejected.

## Signature metadata

`OresActorDeclarationParser.BuildSignatureTable` derives deterministic
signature-only metadata. SHA-256 fingerprints include actor name, visibility,
handler names, value arities and declared scalar message types. Handler
ordering and body implementation/trivia do not change the signature hash.
Every body source span is range-checked before metadata is emitted.

This metadata is not a runtime ActorTypeId, verified actor layout, or executable
Actor IR. It does not prove method body types, actor-owned state layout,
Sendable serialization, ownership, borrow/effect rules, handler control flow,
or mailbox semantics. A future verified Actor IR pass must perform those
checks and bind the typed handler signatures to CFG/guest-frame layouts.

## Compatibility gates

The existing `OresStructuredParser.Parse`, CFG verifier and CIL emitter
continue to reject class/actor declarations with their existing explicit
diagnostic. The new AST parser is an independent compiler-front-end API for
staged actor development; it does not bypass the shipping scalar compiler's
capability gate.

## Resource limits

- Source UTF-8 size: 1 MiB.
- Tokens: 100,000.
- Actors per unit: 256.
- Handlers per actor: 64.
- Value parameters per handler: 16.
- Identifier length: 128.
- Nested comments and handler blocks: 128.

## Next stages

1. Typed actor field/state declarations and explicit mutability/ownership.
2. Semantic handler-body AST and ordinary scalar expression/statement typing.
3. Verified Actor IR with message and field layouts, mailbox policy and frame
   budgets; reject function/closure channel payloads.
4. Static NativeAOT handler method and dispatch-table emission. No reflection,
   dynamic code loading or boxed guest payloads.
5. Supervisor-owned stable ActorRef identity, generation fencing, STOP,
   restart, cancellation and deterministic memory reclamation.
6. Real generated-binary acceptance fixture: actor A exhausts bounded frames,
   actor B returns 42, restart A with fresh state, and old capabilities fail.

No actor runtime, async suspension, machine-code handler execution, NativeAOT
proof or cross-backend parity is claimed by this AST-only stage.
