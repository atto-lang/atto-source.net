# AttoLang .NET NativeAOT nominal actor M0 — narrow compiled CIL slice

**Status:** experimental, authored on [PR #387](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/387). It is not promoted until an exact-head Linux/Windows NativeAOT job actually executes. **Not full actor parity.**

## Source grammar and generated ABI

The new **opt-in** `oresnet check-actor-scalar` and
`oresnet compile-actor-scalar <fixture.atto> --out OresGenerated.dll`
commands admit exactly:

```atto
define actor Counter as
  int value: 0
  fnc increment(int amount): int {
    self.value = self.value + amount;
    return self.value;
  }
end
```

The parser accepts identifiers for the actor and its single field and
parameter, but only the method name `increment` and the exact checked
addition/return statement shape. No actor constructor, supervisor, general
methods, mutable references, `new`, task, async, `suspends`, channels,
pub-sub, exception, dynamic dispatch, macros, inheritance, reflection, actors
inside actors, additional fields or methods, or recursive call graph is
accepted. Source comments are lexed within strict limits, while invalid
Unicode/NUL in *any* part of the input is rejected.

**Nominal identity limitation:** actor name/field/parameter spellings are
validated source metadata, but are not yet exported as first-class nominal
type/field identifiers in the CIL ABI. There is one actor declaration per
compilation unit; generated methods use the fixed `ActorInitial/ActorStep`
bootstrap contract. Renaming the actor without changing its scalar behavior
can currently yield identical generated method bytes. Do not treat this as
full nominal type parity or permit cross-unit actor-type linkage until names,
layouts, generation and capability identity are represented in a validated ABI.

`ActorScalarSourceCompiler` maps the source to a closed, typed, independently
verified scalar CFG consisting of:

- `actor_initial():int` — the source `int value:` initializer
- `actor_step(int current,int amount):int` — a checked addition
- `main():int` — a benign compatibility entry returning 0

The existing System.Reflection.Metadata emitter retains its determinism,
static module link checks and SSA validation. Only in this opt-in mode,
`Oreslang.Generated.OresProgram` additionally exposes *public, statically
linked* CIL MethodDefs `ActorInitial():long` and
`ActorStep(long,long):long`. Only these exact signatures are exportable.
Normal `compile` and `compile-structured` do **not** export actor methods.

## Runtime ownership / native execution proof design

`verification/Ores.Net.ActorCilProbe` statically references the generated
`OresGenerated.dll` during NativeAOT publish; it never dynamically loads or
JIT-compiles that assembly. It registers **two actor instances** through
`ActorRuntime` and gives each:

1. its own `ActorArena` persistent native heap bytes;
2. its own `ActorFrameStack` native frame allocation; and
3. independent mailbox/actor reference and handler-local issued heap handle.

The handler initializes each actor's field from the generated CIL
`ActorInitial`, charges one explicit native invocation frame, reads its own
heap, invokes the generated CIL `ActorStep` with its current state and
bounded typed mail, writes the checked result back, then signals completion.

Two independent message phases must yield:

| | First message | Second message | Final state |
|---|---|---|---|
| Actor A | +1 | +2 | 3 |
| Actor B | +5 | +7 | 12 |

After these four successful messages, an overflow-inducing third message to A
must trap in generated checked CIL and retire only A. Actor B then processes
another message (+1), finishing at 13. **One fault** and **five successful
messages** must be accounted; both actor heaps and native frame stacks must
be refunded after STOP.

Both actors must refund all native heap and stack reservations after STOP,
without leaking a managed `OresGenerated.dll` alongside the NativeAOT
executable.

**Evidence label if actually executed:** `NATIVEAOT_GENERATED_SCALAR_METHODS_WITH_ACTOR_RUNTIME`.
This is stronger than the pre-existing
`SOURCE_VERIFIED_INTERPRETER_INSIDE_NATIVEAOT_HOST` lane, but weaker than
**general .atto actor-to-CIL lowering**: actor creation, method routing,
heap-field load/store and frame admission are still provided by the fixed
NativeAOT C# adapter. General guest actor semantics and physical CLR stack
overflow containment remain **UNPROVEN**.

## CI and negative evidence

`.github/workflows/actor-nominal-nativeaot-m0.yml` pins the actual submitted
PR head and tree, runs the entire managed regression driver, builds a NativeAOT
`oresnet` compiler, generates the actor CIL, statically links an independent
NativeAOT probe and executes two actor heaps on Linux x64 and Windows x64.

The native receipt is exactly:

```
ACTOR_M0_NATIVEAOT_GENERATED_CIL_EXECUTED|actors=2|left=3|right=13|faulted=1|survivor=1|frames=charged|heap=isolated
```

Host metadata tests additionally demand exact CIL export MethodDefs, checked
`add.ovf`, no `callvirt`/`newobj`, deterministic assemblies, source
initializer mutation changing emitted CIL, and negative no-output checks for
unsupported syntax, effect modifiers, duplicate members, injected source,
invalid Unicode/comments, and extra actor declarations.

The existing private-repo GitHub-hosted Actions admission issue may prevent
running this workflow. `runner_id=0`, `steps=[]` and `steps=null` are
**runner-admission failures**, not failed or passing C# tests. No native
execution parity claims are valid until exact-head jobs have actual steps and
the positive and negative receipts.

## Next parity milestones

- **M1** — have the normal actor parser emit a typed actor declaration AST,
  including nominal state layout and methods. Replace the fixed host adapter
  with verified source-level actor instantiation, state-field access and
  dispatch lowering; prove actor A cannot access actor B's private heap.
- **M2** — move generated call descriptors, return PCs and local slots into the
  actor's private stack through compiler-driven entry/return lowering, not
  only explicit host wrapper charging. Recover from bounded actor stack traps
  without letting CLR recursive calls reach OS/process stack exhaustion.
- **M3** — stackless `fnc`/method recursion, mutual recursion, proper tail-call
  frame reuse; reject recursive `routine` cycles.
- **M4** — non-reentrant `suspends` actor frames, generation-fenced
  continuation leases, cancellation/STOP/actor restart and carrier migration.
- **M5** — cross-backend fixture parity with Java/GraalVM and LLVM including
  independent compiler-backed/NativeAOT exact-head receipts, plus ownership,
  pub-sub, ALS/TLS/Task-LS, virtual actor tenant isolation and hot upgrades.

Do not merge on host-only or zero-step evidence. No separate CLR execution
stack has been implemented: an arbitrary user-managed `StackOverflowException`
can still terminate the OS process.
