# Tenant VMs in a .NET process

One .NET process, up to 1000 tenants, with guests running on the shared native core through its C API
rather than as CIL. This is the .NET backend of the design in `oreslang-source.llvm`,
`docs/TENANT_VM_IN_PROCESS_V1.md`. It is a spike, not yet wired into this repository's host.

**Why not CIL for tenants.** CoreCLR has no in-process boundary to build on: AppDomains are gone and an
`AssemblyLoadContext` is not a security boundary. It cannot kill a spinning managed thread, a stack
overflow ends the process, and NativeAOT cannot load code at runtime. This reasoning rests on documented
platform behaviour; a local probe on .NET 10 (JIT and NativeAOT, macOS arm64) confirmed the thread-kill and
stack-overflow behaviour. This is the decided direction: tenants share one process with one VM-isolate
each, and untrusted CIL is refused (conformance tenant isolation v2, oreslang-platform-conformance#352).

## Running it

It needs two things from `oreslang-source.llvm`, which stay in that repository: `libtenantvm`, built
from `TenantVm.cpp`, `TenantVmCApi.cpp` and `TenantVmGuestLoader.cpp`; and a lowered guest with its
digest, from `tools/tenant-vm/lower_guest.py`.

```sh
dotnet publish -c Release -o out
cp /path/to/libtenantvm.so out/
LD_LIBRARY_PATH=out ./out/Spike /path/to/hostile_bounds/guest.so <sha256 from its manifest.json>
```

What it shows, on Ubuntu 24.04 arm64 with .NET 8, 2026-11-03: 1000 VMs and 10,000 actors, with correct
state per tenant, while the CLR ran 545 forced GCs and caught about 1.09M managed null faults on another
thread. Trap, out-of-range read and spin each ended one VM. A wrong digest is refused. NativeAOT passed
on the previous ABI version and was not rerun on this one.

On macOS 26 arm64 with .NET 10, 2026-11-04 (llvm #1343 at `525970b8`): CoreCLR JIT and NativeAOT both pass
with the `hostile_bounds` and `hostile_guard` guests. In guard mode the out-of-range read ends its VM
with `MemoryFault` while the CLR handles its own null faults on another thread. That NativeAOT run used a copy built outside this
repository. Inside it, warnings are errors, and NativeAOT publish fails with NU1903:
`Microsoft.DotNet.ILCompiler` has a high-severity advisory (GHSA-p3jp-9v83-76w9) in both 8.0.27 and
10.0.8. It needs an SDK with a patched ILCompiler; the advisory is not suppressed here. The JIT build
works inside the repository. Not run on x86_64 or in CI.

On macOS, build `libtenantvm.dylib` with `clang++ -std=c++17 -O2 -dynamiclib` and use
`DYLD_LIBRARY_PATH` in place of `LD_LIBRARY_PATH`.
