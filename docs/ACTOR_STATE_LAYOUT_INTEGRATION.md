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
rollback on initialization failure. The generated increment accepts **only** an
`OresVmHost.ActorDispatchScope`. The trusted host retains a
`CreateTrustedScheduler()` capability and calls
`scheduler.Dispatch(isolate, actor, Actor_Counter.Call_increment)`.
The scheduler begins the logical turn and writable state loan, issues
one transient scope, and revokes it before completion or on exception.
The generated increment invokes `scope.AddInt64(0, 1)`, which validates
and updates the field under one host lock. Foreign-thread use and
post-turn/post-cancellation scope replay fail closed. Faults revoke the
scope and terminally cancel the logical turn. This is *not* a generic
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

## Real NativeAOT execution gates (source committed, not yet run)

An independent `verification/Ores.Net.ActorCounterProbe` project links the
generated `Actor_Counter` C# golden and the actual `OresVmHost` runtime
under `PublishAot=true`. The Linux and Windows exact-head workflows now:

1. invoke `emit-actor-csharp` from `examples/actor-counter.ores`;
2. compare generated text against the checked-in C# golden (CRLF normalized
   for Windows);
3. publish the probe to a **native** target executable, not a CLR apphost;
4. run platform-specific native binary/sidecar validation;
5. execute the resulting native program and demand A:1,2,3 / B:1,2,
   independent same-tenant actor state, non-reentrant user turns,
   cancellation recovery, checked i64 overflow and state preservation,
   and new tenant incarnation isolation.

Successful workflow labels are
`LINUX_NET_ACTOR_COUNTER_NATIVE_AOT_GUEST_EXECUTED` and
`WINDOWS_NET_ACTOR_COUNTER_NATIVE_AOT_GUEST_EXECUTED`. Their presence in
workflow source **does not** prove the steps ran. A run only counts when
GitHub reports a nonzero-step exact-head job and captured test output and
native artifact evidence. A source-only canary must precede any private
source transfer to an external test organization; the existing security
incident still blocks such transfers without independent approval.

## Trusted-host scheduler authority boundary (2026-10-31)

The earlier public `InvokeTrustedActorStateLong` delegate entry has been
removed from this integration branch. Generated `Call_increment` takes a
single opaque `ActorDispatchScope` with an **internal constructor**;
it cannot mint a turn, choose a tenant, choose an actor, acquire a new
loan, or retain usable scope access after returning. An internal
`ActorScheduler` constructor prevents simply constructing scheduler
authority. Dispatch is explicitly initiated by a host-held scheduler,
which grants an ephemeral state scope for one synchronous invocation.

`ActorDispatchScope` checks dispatch lifetime and its physical thread
before each operation. Operations are still checked atomically against the
active host-owned turn and loan. Tests cover reentrancy, scope capture
and replay on success/failure, foreign carrier execution, i64 overflow,
isolate restart, and independent actor state.

**Trust boundary limitations:** the embedding application can call
`OresVmHost.CreateTrustedScheduler()`; this method must never be exposed
as an untrusted guest API. The older host modeling methods
`BeginActorTurn`/`BeginActorStateLoan` remain publicly callable by
trusted host code for compatibility, and this is not an OS sandbox or
a defense against arbitrary malicious CLR code with a reference to the
whole host. The actual actor scheduler/mailbox dispatch path is not yet
wired to this source slice. No claim of end-to-end unforgeable actor
authority or physical actor/tenant isolation is warranted until guest
capability confinement and independent scheduler dispatch proofs exist.
Full integration with the production scheduler and a negative untrusted
guest compilation test is still mandatory before promotion.
