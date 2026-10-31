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

## First synchronous, compiler-emitted actor method slice

The experimental CLI command:

```sh
dotnet run --project compiler/Ores.Net.Compiler -- \
    emit-actor-csharp examples/actor-counter.ores \
    --out verification/Ores.Net.Tests/GeneratedCounterActor.g.cs
```

parses one exact, bounded, synchronous `critical handle_state` callback:
`s.count = s.count + 1; return s.count;`. It requires explicitly
declared `using state.read, state.write`, one lexical state parameter,
a declared `int` field, an unescaped state capability, and no extra
statements, members, actor declarations or trailing code. Unsupported
effects, alternate aliases, async/suspending callbacks, arbitrary
arithmetic, and field mismatch fail closed. `OresParser.Parse` and
the production `oresnet compile` command remain unchanged and **do
not** admit guest actor programs into the scalar CIL emitter.

The `VerifiedActorIncrement` IR, constructed only by the strict
parser, is used by `ActorIncrementSourceEmitter.EmitCSharp` to generate
static C# actor construction and the single checked increment method.
The generated actor constructor registers an actor and initializes
separate scalar state through `OresVmHost.ConfigureActorState` with
rollback on initialization failure. The generated increment calls
`OresVmHost.InvokeTrustedActorStateLong`; the host owns the logical
turn and loan, and emitted code invokes `AddActorStateInt64` while the
state check and checked update share one host lock. Faults retire the
loan and terminally cancel the logical turn. This is *not* a generic
actor AST/code generator or a custom ECMA-335 CIL emitter.

`verification/Ores.Net.Tests/GeneratedCounterActor.g.cs` is a
checked-in C# output golden compiled as part of the test executable.
`ActorGeneratedCounterTests.Run()` compares the emitter output against
the golden byte-for-byte (modulo CRLF normalization), rejects unsupported
source forms and, **when executed on a .NET runner**, calls the compiled
fixture to check A:1,2,3; B:1,2, independent same-tenant actors,
nonreentrancy, fault cancellation and restart.

**Not yet proven:** exact-head compilation, production NativeAOT
publishing and real guest execution on this head. The available GitHub
Actions runs have failed with zero executed steps, and this session has
no local `dotnet` SDK. The new emitter targets **static C# build
output**, not the existing native metadata-only CIL backend. No claim
of wider effect-system or multi-backend parity is justified by this
narrow opt-in experimental lane.
