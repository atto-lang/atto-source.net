# Isolated actor state — compiler/runtime integration slice

**Status: draft, host-layout integration only. Not executable Oreslang actor lowering.**

This integration branch combines the following exact source stacks, as
**separate merge parents in the same repository**, without modifying either
underlying PR or moving `main`:

- Compiler admission PR #265 at `b48ee19cd150818d266c74c142577096f006fe5e`
  (including its parent #259).
- NativeAOT runtime PR #264 at `0e4c444ef4cc7d6e0677cb5b9d3e30b69c556302`
  (including its parent #243).
- Two-parent integration commit `bf5d0b0bac556f374fea6074f51b8be4ce3b91e4`.

The only file modified on both histories was
`verification/Ores.Net.Tests/Program.cs`. Both independent test suite
invocations were retained. Runtime-only source, native-host, publishing,
documentation and workflow changes were carried by their exact Git blobs;
compiler-only CI/evidence, parser and actor syntax changes were left intact.

## Typed layout plan

`ActorStateLayoutPlan.FromSchema(schema)` creates immutable, ordered layout
instructions from `OresParser.InspectActorStateSchema(source)`:

- nominal actor identity and actor/state source offsets;
- field name, index, scalar kind (`Int64` or `Boolean`), default, source offset;
- bounded 1–16 fields, unique names, consecutive indices and valid defaults;
- defensive read-only layout and new host-field arrays per materialization.

`ActorStateLayoutPlan.Install(host, isolate, actor)` is a **trusted host
bootstrap** which passes a typed, per-instance copy to
`OresVmHost.ConfigureActorState`. Runtime owns the actor's private cells,
rejects duplicate/late initialization, and validates the actor/isolate
generation. The compiler plan cannot read or write cells or mint scheduler
tickets. Host state access still requires live `ActorTurnTicket` and
`ActorStateLoan` identities, and atomic checked operations.

`ActorStateLayoutIntegrationTests.Run()` is wired into the standard verifier.
It starts with a canonical `.ores` state declaration, inspects the schema,
checks the compiled layout plan, installs it for two actors in one tenant and
one actor in another, checks independent typed defaults, bounded mutation,
cross-actor loan rejection and fresh defaults after tenant incarnation restart.
These are **host integration assertions**, not generated `.ores` guest tests.

## Not yet implemented / promotion blockers

1. Full actor declaration/member IR and lexical `critical` callback parser.
   The schema inspector currently validates the state prefix, not the body.
2. Source-level field type checking, effect rows, nonescaping state loans,
   and compile-time rejection of untrusted access.
3. Generated CIL/NativeAOT actor construction, method lowering, scheduler
   continuations or source-generated `handle_state` operations.
4. Identical LLVM and Java/Graal guest fixtures and backend comparison.
5. Exact-head **executed** compiler and Linux/Windows NativeAOT tests.
   Zero-step/runner-admission failures are not compiler verdicts.
6. Independent security review, branch-protection controls, and resolution
   of private-source transfer incident. No cross-organization mirroring was
   used for this branch.

No source actor may be promoted or considered merge-ready on the basis of
typed schema planning or host-side initialization alone.
