# Shared reactor / SoA validation — 2026-10-29

NOT_EXECUTED: SDK10 and Native AOT are unavailable locally. C# host code, UDP readiness checks and dedicated SDK10/AOT CI are staged. No .NET test pass is claimed.

Host components remain opt-in. Existing runtime/CLI defaults and guest feature admission are not switched to these pools. No compiler-backed five-backend guest parity or hard-real-time guarantee is claimed. The native and Java tests cover bounded saturation, 8,000 concurrent submissions, one polling owner, actual readiness, same-thread polling/execution in one-worker mode, hot-ready-queue I/O fairness, explicit growth, ordinary task failure, self-join rejection and accepted-work draining.

.NET uses the same scenario set but still needs actual SDK10 execution. WASM has admission parity only. Production actor/CONTROL/isolate integration, cancellation/kernel retirement, flags-2-env CLI integration, full backend regression and hardware performance gates remain outstanding.

## Exact component Git blobs

- `runtime/Ores.Net.Runtime/SharedReactorPool.cs`: `3de92b2e0b782ab0bf8b077771efd4b67e6c4acd`
- `runtime-experiment/SharedReactorPoolCheck.cs`: `775b441688b478fc583f5fff743cf27815ceded8`
- `runtime-experiment/SharedReactorPoolCheck.csproj`: `8991d43ad4495cd3c495376571c50701e8fd1496`
