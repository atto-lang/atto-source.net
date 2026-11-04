# Tenant VMs in a .NET process

One .NET process, up to 1000 tenants, with guests running on the shared native core through its C API
rather than as CIL. This is the .NET backend of the design in `oreslang-source.llvm`,
`docs/TENANT_VM_IN_PROCESS_V1.md`. It is a spike, not yet wired into this repository's host.

**Why not CIL for tenants.** CoreCLR has no in-process boundary to build on: AppDomains are gone and an
`AssemblyLoadContext` is not a security boundary. It cannot kill a spinning managed thread, a stack
overflow ends the process, and NativeAOT cannot load code at runtime. This reasoning rests on documented
platform behaviour, not on a CIL experiment. See also the process-worker direction in #348: the two have
not been reconciled, and it is a decision for the project owner.

## Running it

It needs two things from `oreslang-source.llvm`, which stay in that repository: `libtenantvm`, built
from `TenantVm.cpp`, `TenantVmCApi.cpp` and `TenantVmGuestLoader.cpp`; and a lowered guest with its
digest, from `tools/tenant-vm/lower_guest.py`.

```sh
dotnet publish -c Release -o out
cp /path/to/libtenantvm.so out/
LD_LIBRARY_PATH=out ./out/Spike /path/to/hostile_bounds/guest.so <sha256 from its manifest.json>
```

What it shows, on Ubuntu 24.04 arm64 with .NET 8, 2026-11-03, under both CoreCLR JIT and NativeAOT: 1000 VMs
and 10,000 actors, with correct state per tenant, while the CLR ran 751 and 1196 forced GCs and caught 1.5M
and 2.4M managed null faults on another thread. Trap, out-of-range read and spin each ended one VM. A wrong
digest is refused. This has not run on macOS or x86_64, or in CI.
