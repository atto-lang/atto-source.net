using System.Collections.ObjectModel;

namespace Ores.Net.Compiler;

/// <summary>
/// Experimental typed CPU/GPU execution-region graph. This is NOT wired to the
/// shipping arithmetic frontend, native AOT emitter, actor carrier or a GPU.
/// Only checked CPU i64 arithmetic has a reference interpreter.
/// </summary>
public static class CpuGpuExecutionGraph
{
    public enum Type { Void, I64, HostF32, GpuF32, FutureVoid, FutureI64, FutureHostF32, FutureGpuF32 }
    public enum Domain { Cpu, Gpu, Host, Transfer }
    public enum Effect { Host, Device, Transfer, Suspend }
    public enum Op { ConstI64, AddI64, Param, Upload, Download, DeviceKernel, DeviceFence, DeviceAdmission, Submit, Await }

    public abstract record Expr;
    public sealed record CpuRegion(Expr Body, bool Nb = false) : Expr;
    public sealed record GpuRegion(Expr Body, bool Nb = false) : Expr;
    public sealed record I64Literal(long Value) : Expr;
    public sealed record Add(Expr Left, Expr Right) : Expr;
    public sealed record Parameter(string Name, Type Type) : Expr;
    public sealed record Upload(Expr Value) : Expr;
    public sealed record Download(Expr Value) : Expr;
    public sealed record DeviceKernel(string Name, Expr Value) : Expr;
    public sealed record Await(Expr Value) : Expr;
    public sealed record Sequence(IReadOnlyList<Expr> Values) : Expr;

    public sealed record Step(int Id, Op Opcode, Domain Domain, Type ResultType,
        IReadOnlyList<int> Dependencies, int Left = -1, int Right = -1,
        string? Name = null, long? Literal = null);

    public sealed class Graph
    {
        internal Graph(IReadOnlyList<Step> steps, Type resultType, int returnId,
            IReadOnlyList<Effect> effects, int deviceKernels)
        {
            Steps = Array.AsReadOnly(steps.ToArray());
            ResultType = resultType;
            ReturnId = returnId;
            Effects = Array.AsReadOnly(effects.ToArray());
            DeviceKernels = deviceKernels;
        }
        public IReadOnlyList<Step> Steps { get; }
        public Type ResultType { get; }
        public int ReturnId { get; }
        public IReadOnlyList<Effect> Effects { get; }
        public int DeviceKernels { get; }
    }
    private readonly record struct Value(Type ResultType, int Id);

    private static Type Future(Type value) => value switch
    {
        Type.Void => Type.FutureVoid,
        Type.I64 => Type.FutureI64,
        Type.HostF32 => Type.FutureHostF32,
        Type.GpuF32 => Type.FutureGpuF32,
        _ => throw new FormatException("Nested Future requires explicit composition")
    };
    private static Type Awaited(Type value) => value switch
    {
        Type.FutureVoid => Type.Void,
        Type.FutureI64 => Type.I64,
        Type.FutureHostF32 => Type.HostF32,
        Type.FutureGpuF32 => Type.GpuF32,
        _ => throw new FormatException("Await requires Future<T>")
    };
    private static bool Owned(Type value) => value is Type.HostF32 or Type.GpuF32;
    private static bool NameIsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        static bool Letter(char x) => x is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';
        if (!Letter(name[0])) return false;
        return name.Skip(1).All(c => Letter(c) || c is (>= '0' and <= '9'));
    }

    public static Graph Lower(Expr root, IReadOnlyDictionary<string, Type> parameters)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count > 64 || parameters.Any(p => !NameIsValid(p.Key) ||
              p.Value is Type.Void or Type.FutureVoid or Type.FutureI64 or Type.FutureHostF32 or Type.FutureGpuF32))
            throw new FormatException("Invalid CPU/GPU graph parameters");
        return new Builder(parameters).Build(root);
    }
    private sealed class Builder
    {
        private readonly IReadOnlyDictionary<string, Type> parameters;
        private readonly HashSet<string> moved = new(StringComparer.Ordinal);
        private readonly List<Step> nodes = new();
        private readonly HashSet<Effect> effects = new();
        private int kernels;

        internal Builder(IReadOnlyDictionary<string, Type> parameters)
        {
            this.parameters = new ReadOnlyDictionary<string, Type>(
                new Dictionary<string, Type>(parameters, StringComparer.Ordinal));
        }

        private Value Emit(Op op, Domain domain, Type type,
            int left = -1, int right = -1, string? name = null, long? literal = null)
        {
            if (nodes.Count >= 2048) throw new FormatException("Graph node budget exceeded");
            var at = nodes.Count;
            var deps = new SortedSet<int>();
            if (left >= 0) deps.Add(left);
            if (right >= 0) deps.Add(right);
            if (at > 0) deps.Add(at - 1);
            if (deps.Any(d => d < 0 || d >= at))
                throw new FormatException("Cyclic or invalid graph dependencies");
            nodes.Add(new Step(at, op, domain, type, Array.AsReadOnly(deps.ToArray()),
                left, right, name, literal));
            return new Value(type, at);
        }
        internal Graph Build(Expr root)
        {
            Value result = Visit(root, Domain.Cpu, 0);
            return new Graph(nodes, result.ResultType, result.Id,
                effects.OrderBy(e => e).ToArray(), kernels);
        }
        private Value Visit(Expr expr, Domain domain, int depth)
        {
            if (depth > 64) throw new FormatException("Execution-region AST depth exceeded");
            if (expr is I64Literal n)
            {
                if (domain != Domain.Cpu) throw new FormatException("Host scalar in GPU region");
                effects.Add(Effect.Host);
                return Emit(Op.ConstI64, Domain.Cpu, Type.I64, literal: n.Value);
            }
            if (expr is Parameter p)
            {
                if (!NameIsValid(p.Name) || !parameters.TryGetValue(p.Name, out var declared) ||
                    declared != p.Type) throw new FormatException("Undeclared/mistyped parameter");
                if (Owned(p.Type) && !moved.Add(p.Name))
                    throw new FormatException("Use of moved owned buffer parameter");
                return Emit(Op.Param, Domain.Host, p.Type, name: p.Name);
            }
            if (expr is Add a)
            {
                if (domain != Domain.Cpu) throw new FormatException("Host arithmetic inside GPU region");
                var l = Visit(a.Left, domain, depth + 1);
                var r = Visit(a.Right, domain, depth + 1);
                if (l.ResultType != Type.I64 || r.ResultType != Type.I64)
                    throw new FormatException("Add operands require i64");
                effects.Add(Effect.Host);
                return Emit(Op.AddI64, Domain.Cpu, Type.I64, l.Id, r.Id);
            }
            if (expr is Upload u)
            {
                if (domain != Domain.Cpu) throw new FormatException("Upload requires host continuation");
                var v = Visit(u.Value, domain, depth + 1);
                if (v.ResultType != Type.HostF32) throw new FormatException("Host/device residency mismatch");
                effects.Add(Effect.Transfer); effects.Add(Effect.Suspend);
                return Emit(Op.Upload, Domain.Transfer, Type.GpuF32, v.Id);
            }
            if (expr is Download d)
            {
                if (domain != Domain.Cpu) throw new FormatException("Download requires host continuation");
                var v = Visit(d.Value, domain, depth + 1);
                if (v.ResultType != Type.GpuF32) throw new FormatException("Host/device residency mismatch");
                effects.Add(Effect.Transfer); effects.Add(Effect.Suspend);
                return Emit(Op.Download, Domain.Transfer, Type.HostF32, v.Id);
            }
            if (expr is DeviceKernel k)
            {
                if (domain != Domain.Gpu || !NameIsValid(k.Name))
                    throw new FormatException("Kernel requires GPU region and checked name");
                var v = Visit(k.Value, domain, depth + 1);
                if (v.ResultType != Type.GpuF32)
                    throw new FormatException("Device kernel requires GPU buffer");
                effects.Add(Effect.Device); effects.Add(Effect.Suspend);
                kernels++;
                return Emit(Op.DeviceKernel, Domain.Gpu, Type.GpuF32, v.Id, name: k.Name);
            }
            if (expr is Await a)
            {
                if (domain != Domain.Cpu) throw new FormatException("Await requires host continuation");
                var v = Visit(a.Value, domain, depth + 1);
                effects.Add(Effect.Suspend);
                return Emit(Op.Await, Domain.Cpu, Awaited(v.ResultType), v.Id);
            }
            if (expr is Sequence s)
            {
                if (s.Values == null || s.Values.Count > 2048)
                    throw new FormatException("Region statement limit");
                var last = new Value(Type.Void, -1);
                foreach (var item in s.Values) last = Visit(item, domain, depth + 1);
                return last;
            }
            if (expr is CpuRegion c) return Region(c.Body, c.Nb, Domain.Cpu, domain, depth);
            if (expr is GpuRegion g) return Region(g.Body, g.Nb, Domain.Gpu, domain, depth);
            throw new FormatException("Unknown execution-region AST");
        }
        private Value Region(Expr body, bool nb, Domain next, Domain prior, int depth)
        {
            if (prior == Domain.Gpu && next == Domain.Cpu)
            {
                effects.Add(Effect.Suspend);
                Emit(Op.DeviceFence, Domain.Transfer, Type.Void);
            }
            if (prior == Domain.Cpu && next == Domain.Gpu)
            {
                effects.Add(Effect.Suspend);
                Emit(Op.DeviceAdmission, Domain.Transfer, Type.Void);
            }
            var before = kernels;
            var v = Visit(body, next, depth + 1);
            if (next == Domain.Gpu && before == kernels)
                throw new FormatException("Empty GPU region has no kernel work");
            if (!nb) return v;
            effects.Add(Effect.Suspend);
            return Emit(Op.Submit, Domain.Host, Future(v.ResultType), v.Id);
        }
    }

    /// <summary>CPU reference only: never permits device, transfer or async work.</summary>
    public static long ExecuteCpuI64(Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.ResultType != Type.I64 || graph.ReturnId < 0 ||
            graph.ReturnId >= graph.Steps.Count || graph.Steps.Count > 2048)
            throw new FormatException("CPU oracle requires valid i64 graph");
        var values = new long[graph.Steps.Count];
        for (int at = 0; at < graph.Steps.Count; at++)
        {
            var step = graph.Steps[at];
            if (step.Id != at || step.Domain != Domain.Cpu ||
                step.Dependencies.Any(d => d < 0 || d >= at))
                throw new FormatException("No GPU/transfer/async CPU fallback");
            if (step.Opcode == Op.ConstI64)
            {
                if (step.Literal == null || step.ResultType != Type.I64)
                    throw new FormatException("Malformed i64 constant");
                values[at] = step.Literal.Value;
            }
            else if (step.Opcode == Op.AddI64)
            {
                if (step.Left < 0 || step.Right < 0 || step.Left >= at ||
                    step.Right >= at || !step.Dependencies.Contains(step.Left) ||
                    !step.Dependencies.Contains(step.Right))
                    throw new FormatException("Malformed i64 SSA operands");
                values[at] = checked(values[step.Left] + values[step.Right]);
            }
            else throw new FormatException("CPU oracle does not support this opcode");
        }
        return values[graph.ReturnId];
    }
}
