# .NET `std/time` real-CIL admission

The compiler independently owns `stdlib/std/time.atto`, but the current
Atto.Cil.Compiler accepts a **bounded scalar profile**, not its nominal
class/Result/Timer/scheduler semantics. `import module time from "std/time"`
cannot be reported as linked or executed.

`tests/std_time_cil_admission.sh` compiles the **real compiler project**,
executes a signed-int64 scalar `.atto` program through generated CIL plus the
real `Atto.Cil.Guest.Host` and expects exit code **42**. It then runs the real
compiler with the entire `std/time` source, an attempted `import module`
and a `time.sleep` call. Every unsupported attempt must fail with a compiler
diagnostic and **must not publish an assembly**.

The gate uses .NET SDK 10 to target net10.0 as an isolated test override and
does NOT change the checked-in target framework. The positive scalar guest
does not establish `std/time` implementation, asynchronous actor sleep, or
strict NativeAOT. Implement import lowering, nominal type layout and scheduler
intrinsics before any positive full time-guest claim.
