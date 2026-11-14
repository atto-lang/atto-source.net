# .NET Task/Future source-language slice (draft)

Tracking: issue #183 and host foundation PR #186. This change is layered on
`interop/i01-observer-isolation-20261027` and is **not** a full I-01–I-08 pass.

## Actually implemented

* `OresTask<T>` is a nominal producer handle, `OresFuture<T>` an observer-only
  view; neither derives from `System.Threading.Tasks.Task<T>`.
* `OresTaskScope` reserves bounded producer and observer slots **before**
  scheduling/attaching. A scope has one owned serialized work pump, one
  cancellation authority and a separate per-producer retirement receipt.
* `FromTask` creates a distinct view sharing an already-started producer;
  no producer work is scheduled by conversion. Each `ObserveAsync` returns
  a separate result projection. Cancellation of any observer never touches
  the scope's producer cancellation source. An OCE without scope cancellation
  remains a fault. Observer slot is released before publishing its terminal state.
* The NativeAOT compiler CLI has an intentionally separate interpreter command
  `oresnet run-async input.ores`. It only accepts this exact typed structure
  (with lexical whitespace/comments): 
   
  ```ores
  fnc main(): Future<int> {
      return Future.from_task(task { return 42; });
  }
  ```

  The numeric i64 literal can vary within range. The runner creates exactly
  one scope-owned producer and one observer, awaits value 42 and the producer's
  own retirement receipt, checks admission counters, then prints `42`.

## Boundaries still closed

* `oresnet compile` and `oresnet check` DO NOT admit any Task source. They
  do not emit an async `OresGenerated.dll`. The new `run-async` command
  interprets a whitelisted AST inside the **NativeAOT compiler executable**;
  it is real source-driven execution, but **not compiled guest CIL**.
* This scheduler substrate is one host work pump, not actor-affine user
  continuation dispatch, and does not implement sealed state, structured
  cancellation, safe user closures, Send/Sync, generations, real-time
  guarantees or cross-backend parity. The Task body is a verified constant
  only; arbitrary guest code stays rejected.
* `Task.from_future`, Observable adapters, `#single`, `#first`, dynamic
  sources, `await`, arbitrary expressions and nesting remain unsupported.
  Cancellation of scope-owned queued producers yields terminal cancellation,
  not physical abandonment. Producer receipts never derive from observer
  detachment.
* Guest `Future<T>` is not a freely copyable guest capability; the ownership
  and affine rules will be added before admitting richer source expressions.

## Verification / evidence

Run:
```powershell
dotnet run --project verification/Ores.Net.Tests -c Release
dotnet publish compiler/Ores.Net.Compiler -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o artifacts/async-native-compiler
./verification/assert-async-source-slice.ps1 -Compiler ./artifacts/async-native-compiler/oresnet
```

The dedicated Linux/Windows NativeAOT workflow records exact source commit/tree,
NativeAOT compiler executable SHA-256, guest slice value, and explicit false
flags for generated guest CIL and full interop gates. A zero-step run is
NOT_EXECUTED. Until real execution is verified, all results are **PENDING**.
No cross-org CI mirror is implied by the source branch alone.

## Next implementation gate

Add a typed async HIR/MIR function entry and metadata-only `RunAsync`
CIL emission into a statically linked NativeAOT host, retaining the same scope,
observer, producer retirement, quota, exception and negative cases. Replace the
whitelist interpreter only once actual compiled execution is independently
verified. Then proceed with `Task.from_future` (one tracked waiter) and the
Observable cardinality/affinity contract.
