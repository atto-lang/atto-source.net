# Shared reactor / SoA validation — 2026-10-29

NOT_EXECUTED: SDK10 and Native AOT are unavailable locally. C# host code, UDP readiness checks and dedicated SDK10/AOT CI are staged. No .NET test pass is claimed.

Host components remain opt-in. Existing runtime/CLI defaults and guest feature admission are not switched to these pools. No compiler-backed five-backend guest parity or hard-real-time guarantee is claimed. The native and Java tests cover bounded saturation, 8,000 concurrent submissions, one polling owner, actual readiness, same-thread polling/execution in one-worker mode, hot-ready-queue I/O fairness, explicit growth, ordinary task failure, self-join rejection and accepted-work draining.

.NET uses the same scenario set but still needs actual SDK10 execution. WASM has admission parity only. Production actor/CONTROL/isolate integration, cancellation/kernel retirement, flags-2-env CLI integration, full backend regression and hardware performance gates remain outstanding.

## Exact component Git blobs

- `runtime/Ores.Net.Runtime/SharedReactorPool.cs`: `5e7c2e46ec718983d92aa4c6a8f3f5deb58640d8`
- `runtime-experiment/SharedReactorPoolCheck.cs`: `0752d8cdedeb3440dc25de65f23e01eaa4b7f5c7`
- `runtime-experiment/SharedReactorPoolCheck.csproj`: `8991d43ad4495cd3c495376571c50701e8fd1496`

## Incremental wake-gating hardening — 2026-11-03

Hot-queue submissions avoid calling the reactor wake function when the single polling lease is absent. The polling lease is acquired before the poller's ready-queue check; active-lease submissions still wake it after publication. Added a deterministic saturation regression: while the only executor is blocked in user work, 1024 accepted queue entries do not trigger reactor wakeups. An ordinary shutdown continues to wake an active reactor.

Host implementation and regression assertion updated for wake gating while no polling lease exists. SDK10 and Native AOT are unavailable locally; NOT_EXECUTED. Focused CI reported no steps, not a failed code/test execution.

These results are standalone host-substrate evidence only. The PR remains draft and opt-in; runtime wiring, actor authority, CONTROL priority, cancellation generation fencing, and total-process thread measurements are still required. Remote zero-step workflow failures are not execution evidence.
