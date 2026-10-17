using Ores.Net.Compiler;
using static Ores.Net.Compiler.CpuGpuExecutionGraph;
using RegionType = Ores.Net.Compiler.CpuGpuExecutionGraph.Type;

internal static class CpuGpuExecutionGraphTests
{
    private static void Require(bool ok, string description)
    {
        if (!ok) throw new Exception("CPU/GPU typed graph: " + description);
    }
    private static void Reject(Action action, string description)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new Exception("Unsupported CPU/GPU graph admitted: " + description);
    }
    internal static void Run()
    {
        var cpu = Lower(new CpuRegion(new Add(new I64Literal(40), new I64Literal(2)),
            false), new Dictionary<string, RegionType>());
        Require(cpu.ResultType == RegionType.I64 && ExecuteCpuI64(cpu) == 42,
            "checked scalar host execution");
        Require(cpu.DeviceKernels == 0, "CPU work cannot claim GPU launch");

        var future = Lower(new CpuRegion(new I64Literal(42), true),
            new Dictionary<string, RegionType>());
        Require(future.ResultType == RegionType.FutureI64 &&
                future.Steps.Last().Opcode == Op.Submit,
            "nb cpu must return one Future<i64>, never a Stream");
        Reject(() => ExecuteCpuI64(future), "no CPU async fallback");
        var awaited = Lower(new CpuRegion(
            new Await(new CpuRegion(new I64Literal(11), true)), false),
            new Dictionary<string, RegionType>());
        Require(awaited.ResultType == RegionType.I64 &&
                awaited.Steps.Last().Opcode == Op.Await,
            "host await unwraps typed Future result");

        var first = new Parameter("first", RegionType.GpuF32);
        var download = new Parameter("download", RegionType.GpuF32);
        var second = new Parameter("second", RegionType.GpuF32);
        var nested = Lower(new GpuRegion(new Sequence(new Expr[] {
            new DeviceKernel("kernelA", first),
            new CpuRegion(new Sequence(new Expr[] {
                new Download(download),
                new GpuRegion(new DeviceKernel("kernelB", second))
            }))
        })), new Dictionary<string, RegionType> {
            ["first"]=RegionType.GpuF32, ["download"]=RegionType.GpuF32, ["second"]=RegionType.GpuF32
        });
        Require(nested.ResultType == RegionType.GpuF32 &&
                nested.DeviceKernels == 2,
            "typed GPU->CPU->GPU graph placement");
        Require(nested.Steps.Any(s => s.Opcode == Op.DeviceFence) &&
                nested.Steps.Count(s => s.Opcode == Op.DeviceAdmission) == 2,
            "nested region requires host fence and re-admission");
        Require(nested.Steps.All(s => s.Dependencies.All(d => d < s.Id)),
            "execution graph is a DAG");
        Reject(() => ExecuteCpuI64(nested), "no GPU fallback");

        var host = new Parameter("host", RegionType.HostF32);
        Reject(() => Lower(new GpuRegion(new DeviceKernel("invalid", host)),
            new Dictionary<string, RegionType> { ["host"]=RegionType.HostF32 }),
            "no implicit upload");
        Reject(() => Lower(new CpuRegion(new Download(host)),
            new Dictionary<string, RegionType> { ["host"]=RegionType.HostF32 }),
            "no implicit download");
        Reject(() => Lower(new GpuRegion(new Sequence(Array.Empty<Expr>())),
            new Dictionary<string, RegionType>()), "empty GPU kernel");
        Reject(() => Lower(new CpuRegion(new Await(new I64Literal(4))),
            new Dictionary<string, RegionType>()), "await requires future");
        Reject(() => Lower(new GpuRegion(new Sequence(new Expr[] {
            new DeviceKernel("first", new Parameter("device", RegionType.GpuF32)),
            new DeviceKernel("second", new Parameter("device", RegionType.GpuF32))
        })), new Dictionary<string, RegionType> { ["device"]=RegionType.GpuF32 }),
            "use after implicit move");

        var overflow = Lower(new CpuRegion(new Add(
            new I64Literal(long.MaxValue), new I64Literal(1))),
            new Dictionary<string, RegionType>());
        try { ExecuteCpuI64(overflow); }
        catch (OverflowException) { Console.WriteLine("CPU/GPU graph checked overflow verified"); return; }
        throw new Exception("CPU/GPU graph allowed signed i64 overflow");
    }
}
