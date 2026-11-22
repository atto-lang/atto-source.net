# .NET native Atto std.fs byte policy

The compiler-local `stdlib/std/fs_io_policy.atto` implements five pure functions for bounded I/O chunk selection, confirmed partial write progress, EOF-compatible read completion and overflow-safe append admission. Unlike the previous `fs_text_policy.atto` it addresses byte-stream loop decisions; it is independently owned in this backend.

`fixtures/cil/fs-io-policy-main.atto` checks 31 boundaries and returns 42 on success. `scripts/test-cil-guest.sh` concatenates the real `.atto` definitions with this guest fixture, compiles them through the native Atto→CIL compiler, runs CoreCLR, rebuilds NativeAOT and checks exit 42. These tests have been authored but **not yet executed** on the current commit.

## Security and implementation boundary

A pure admissibility result does not grant file authority. The host must independently enforce tenant READ/WRITE grants, descriptor-root path confinement, symlink/rename race safety, actor restart generation, bounded async execution, cancellation, resource release, and quota rechecking. No host filesystem primitive or fake `std.fs` export is introduced here. The `import * as fs from pkg "std.fs";` runtime remains unsupported until typed host ABI, package linking, `susp` continuation and actual compiled guest I/O are integrated.

This work complements [spec #117](https://github.com/atto-lang/atto-spec/pull/117) and [common ABI #29](https://github.com/atto-lang/atto-common-source/pull/29), and keeps the compiler implementation independent from the other `atto-source.*` repositories.
