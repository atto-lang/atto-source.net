namespace Ores.Net.Runtime;

/// <summary>
/// Typed host execution-graph IR v1, shared semantically with Java and LLVM.
/// This is not an Oreslang source AST, a GPU driver, or a guest callback API.
/// The trusted runtime supplies a bounded CPU executor; GPU and all transfers
/// are explicit graph nodes but unavailable without a real device provider.
/// </summary>
public static class ExecutionGraphV1
{
    public enum Domain { Cpu, Gpu }
    public enum Kind { Unit, HostI64, DeviceI64 }
    public enum Operation { Unit, ConstantI64, AddI64, UploadI64, GpuAddI64, DownloadI64 }

    public sealed class Unit
    {
        private Unit() { }
        public static Unit Instance { get; } = new();
    }

    public sealed class DeviceI64
    {
        private DeviceI64() { }
    }

    public sealed record Node(int Id, Domain Domain, Operation Operation,
        Kind ResultKind, IReadOnlyList<int> Inputs, long Constant);

    public sealed class Ref<T>
    {
        internal readonly Builder Owner;
        internal readonly int Index;
        internal readonly Kind Kind;
        internal Ref(Builder owner, int index, Kind kind)
        {
            Owner = owner;
            Index = index;
            Kind = kind;
        }
    }

    public sealed class Builder
    {
        private readonly List<Node> _nodes = new();
        private bool _sealed;

        private Ref<T> Append<T>(Domain domain, Operation operation, Kind kind,
            long constant, params int[] dependencies)
        {
            if (_sealed) throw new InvalidOperationException("execution graph builder sealed");
            if (_nodes.Count >= 4096) throw new ArgumentException("execution graph node budget exceeded");
            foreach (int dependency in dependencies)
                if (dependency < 0 || dependency >= _nodes.Count)
                    throw new ArgumentException("forward execution graph dependency");
            int index = _nodes.Count;
            _nodes.Add(new Node(index, domain, operation, kind,
                Array.AsReadOnly((int[])dependencies.Clone()), constant));
            return new Ref<T>(this, index, kind);
        }

        private void Validate<T>(Ref<T> input, Kind kind)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!ReferenceEquals(input.Owner, this) ||
                input.Index < 0 || input.Index >= _nodes.Count || input.Kind != kind)
                throw new ArgumentException("invalid graph ownership, domain or value kind");
        }

        public Ref<Unit> CpuUnit() => Append<Unit>(Domain.Cpu, Operation.Unit, Kind.Unit, 0);
        public Ref<long> CpuI64(long value) =>
            Append<long>(Domain.Cpu, Operation.ConstantI64, Kind.HostI64, value);

        public Ref<long> CpuAdd(Ref<long> a, Ref<long> b)
        {
            Validate(a, Kind.HostI64);
            Validate(b, Kind.HostI64);
            return Append<long>(Domain.Cpu, Operation.AddI64, Kind.HostI64, 0, a.Index, b.Index);
        }

        public Ref<DeviceI64> UploadI64(Ref<long> host)
        {
            Validate(host, Kind.HostI64);
            return Append<DeviceI64>(Domain.Gpu, Operation.UploadI64,
                Kind.DeviceI64, 0, host.Index);
        }

        public Ref<DeviceI64> GpuAddI64(Ref<DeviceI64> a, Ref<DeviceI64> b)
        {
            Validate(a, Kind.DeviceI64);
            Validate(b, Kind.DeviceI64);
            return Append<DeviceI64>(Domain.Gpu, Operation.GpuAddI64,
                Kind.DeviceI64, 0, a.Index, b.Index);
        }

        public Ref<long> DownloadI64(Ref<DeviceI64> device)
        {
            Validate(device, Kind.DeviceI64);
            return Append<long>(Domain.Cpu, Operation.DownloadI64,
                Kind.HostI64, 0, device.Index);
        }

        public Plan<T> Build<T>(Ref<T> output)
        {
            if (_sealed) throw new InvalidOperationException("execution graph builder sealed");
            ArgumentNullException.ThrowIfNull(output);
            if (!ReferenceEquals(output.Owner, this) || output.Index >= _nodes.Count)
                throw new ArgumentException("graph output belongs to another builder");
            _sealed = true;
            return new Plan<T>(_nodes.ToArray(), output.Index, output.Kind);
        }
    }

    public sealed class Ticket<T>
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<T> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _cancelled;
        private bool _settled;
        private int _started;
        internal bool ClaimExecution() => System.Threading.Interlocked.Exchange(ref _started, 1) == 0;
        public Task<T> Task => _result.Task;
        internal bool Cancelled => _cancelled;

        public bool Cancel()
        {
            lock (_gate)
            {
                if (_settled) return false;
                _settled = true;
                _cancelled = true;
                _result.TrySetCanceled();
                return true;
            }
        }

        internal void Complete(T value)
        {
            lock (_gate)
            {
                if (_settled) return;
                _settled = true;
                _result.TrySetResult(value);
            }
        }

        internal void Fail(Exception failure)
        {
            lock (_gate)
            {
                if (_settled) return;
                _settled = true;
                _result.TrySetException(failure);
            }
        }

        internal void RejectExecutor(Exception failure)
        {
            lock (_gate)
            {
                // A misbehaving executor can enqueue the task and then throw.
                // Even after terminal rejection, the queued closure must not
                // evaluate CPU work if it is dispatched subsequently.
                _cancelled = true;
                if (_settled) return;
                _settled = true;
                _result.TrySetException(failure);
            }
        }
    }

    public sealed class Plan<T>
    {
        private readonly IReadOnlyList<Node> _nodes;
        private readonly int _output;
        public Kind ResultKind { get; }

        internal Plan(Node[] nodes, int output, Kind kind)
        {
            // A trusted/imported graph may provide a separately declared kind.
            // Tie it to Plan<T> before admitting the node array: otherwise a
            // forged Plan<long> over a Unit result passes IR validation and
            // fails later as InvalidCastException during execution.
            Kind expected = typeof(T) == typeof(Unit) ? Kind.Unit
                : typeof(T) == typeof(long) ? Kind.HostI64
                : typeof(T) == typeof(DeviceI64) ? Kind.DeviceI64
                : throw new ArgumentException("unsupported execution graph CLR result type");
            if (kind != expected)
                throw new ArgumentException("execution graph result kind does not match Plan<T>");
            ArgumentNullException.ThrowIfNull(nodes);
            if (nodes.Length == 0 || nodes.Length > 4096 || output < 0 || output >= nodes.Length)
                throw new ArgumentException("invalid typed graph size or output");
            // Freeze all caller-owned operand lists before admission. A shallow
            // Node[] copy still permits a mutable IReadOnlyList<int> to be
            // changed after signature validation, turning a safe graph into
            // an out-of-bounds or type-confused execution plan.
            var frozen = new Node[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                Node original = nodes[i] ?? throw new ArgumentException("null typed graph node");
                var sourceInputs = original.Inputs ??
                    throw new ArgumentException("null typed graph input list");
                if (sourceInputs.Count > 2)
                    throw new ArgumentException("invalid typed graph arity");
                var copiedInputs = new int[sourceInputs.Count];
                for (int j = 0; j < copiedInputs.Length; j++)
                    copiedInputs[j] = sourceInputs[j];
                frozen[i] = new Node(original.Id, original.Domain, original.Operation,
                    original.ResultKind, Array.AsReadOnly(copiedInputs), original.Constant);
            }
            nodes = frozen;
            for (int i = 0; i < nodes.Length; i++)
            {
                Node node = nodes[i];
                if (node.Id != i) throw new ArgumentException("invalid typed graph node ID");
                foreach (int dependency in node.Inputs)
                    if (dependency < 0 || dependency >= i)
                        throw new ArgumentException("invalid typed graph dependency");

                switch (node.Operation)
                {
                    case Operation.Unit:
                        Signature(nodes, node, Domain.Cpu, Kind.Unit);
                        break;
                    case Operation.ConstantI64:
                        Signature(nodes, node, Domain.Cpu, Kind.HostI64);
                        break;
                    case Operation.AddI64:
                        Signature(nodes, node, Domain.Cpu, Kind.HostI64, Kind.HostI64, Kind.HostI64);
                        break;
                    case Operation.UploadI64:
                        Signature(nodes, node, Domain.Gpu, Kind.DeviceI64, Kind.HostI64);
                        break;
                    case Operation.GpuAddI64:
                        Signature(nodes, node, Domain.Gpu, Kind.DeviceI64, Kind.DeviceI64, Kind.DeviceI64);
                        break;
                    case Operation.DownloadI64:
                        Signature(nodes, node, Domain.Cpu, Kind.HostI64, Kind.DeviceI64);
                        break;
                    default:
                        throw new ArgumentException("invalid typed graph opcode");
                }
            }
            if (nodes[output].ResultKind != kind) throw new ArgumentException("invalid typed graph output kind");
            _nodes = Array.AsReadOnly(nodes);
            _output = output;
            ResultKind = kind;
        }

        private static void Signature(Node[] nodes, Node node, Domain domain, Kind kind,
                                      params Kind[] inputs)
        {
            if (node.Domain != domain || node.ResultKind != kind || node.Inputs.Count != inputs.Length)
                throw new ArgumentException("invalid typed graph operation signature");
            for (int i = 0; i < inputs.Length; i++)
            {
                int dependency = node.Inputs[i];
                if (dependency < 0 || dependency >= node.Id || nodes[dependency].ResultKind != inputs[i])
                    throw new ArgumentException("invalid typed graph operand kind");
            }
        }

        public IReadOnlyList<Node> Nodes => _nodes;

        public bool CpuOnly => _nodes.All(n => n.Domain == Domain.Cpu &&
            n.Operation != Operation.DownloadI64);

        private void AdmitCpu()
        {
            if (!CpuOnly)
                throw new NotSupportedException(
                    "GPU execution/transfer provider unavailable; no implicit CPU fallback");
        }

        private T Evaluate(Func<bool> cancelled)
        {
            object?[] values = new object[_nodes.Count];
            foreach (var node in _nodes)
            {
                if (cancelled()) throw new OperationCanceledException("CPU graph cancelled");
                values[node.Id] = node.Operation switch
                {
                    Operation.Unit => Unit.Instance,
                    Operation.ConstantI64 => node.Constant,
                    Operation.AddI64 => checked(
                        (long)values[node.Inputs[0]]! + (long)values[node.Inputs[1]]!),
                    _ => throw new InvalidOperationException("unadmitted device operation")
                };
            }
            if (cancelled()) throw new OperationCanceledException("CPU graph cancelled");
            return (T)values[_output]!;
        }

        /// <summary>Host-only synchronous execution; never run on an actor carrier.</summary>
        public T ExecuteCpu()
        {
            AdmitCpu();
            return Evaluate(() => false);
        }

        /// <summary>
        /// Nonblocking, one-shot CPU submission. Caller supplies a trusted
        /// bounded executor; this code never creates a thread or an implicit
        /// unbounded Task.Run queue. Ticket cancellation suppresses publication.
        /// </summary>
        public Ticket<T> SubmitCpu(Action<Action> schedule)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            var ticket = new Ticket<T>();
            // A scheduler can start its callback before returning, then
            // throw. Until admission resolves, not even a pure host graph
            // may evaluate or publish a result. This matches Java/LLVM/Forge.
            // A TaskCompletionSource<bool> is a permanent one-shot admission
            // outcome for late callbacks: true only after schedule returns
            // normally, false on any scheduler/inline rejection. Unlike a
            // method-scoped disposable wait handle, queued callbacks may
            // retain this safely after SubmitCpu returns.
            var admission = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int inlineAttempted = 0;
            bool accepted = false;
            int submittingThread = Environment.CurrentManagedThreadId;
            // Admission decision (0 pending, 1 accepted, 2 rejected), published before the
            // completion signal so callbacks never block on or read a Task result.
            int admittedState = 0;
            // Runs only after admission has completed; never waits.
            void RunAdmitted()
            {
                try
                {
                    if (System.Threading.Volatile.Read(ref admittedState) != 1
                            || ticket.Cancelled) return;
                    ticket.Complete(Evaluate(() => ticket.Cancelled));
                }
                catch (OperationCanceledException) { ticket.Cancel(); }
                catch (Exception ex) { ticket.Fail(ex); }
            }
            try
            {
                AdmitCpu();
                schedule(() =>
                {
                    if (!ticket.ClaimExecution() || ticket.Cancelled) return;
                    try
                    {
                        if (!admission.Task.IsCompleted &&
                                Environment.CurrentManagedThreadId == submittingThread)
                        {
                            // Defer terminal error so an inline-then-throw
                            // executor preserves its own rejection exception.
                            System.Threading.Interlocked.Exchange(ref inlineAttempted, 1);
                            return;
                        }
                        if (!admission.Task.IsCompleted)
                        {
                            // Async-only runtime boundary: never park this executor thread on
                            // admission. SubmitCpu always settles it in its finally block, so
                            // re-enter through the same executor once the decision exists.
                            admission.Task.ContinueWith(
                                _ =>
                                {
                                    try { schedule(RunAdmitted); }
                                    catch (Exception ex) { ticket.RejectExecutor(ex); }
                                },
                                System.Threading.CancellationToken.None,
                                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                                System.Threading.Tasks.TaskScheduler.Default);
                            return;
                        }
                        RunAdmitted();
                    }
                    catch (OperationCanceledException) { ticket.Cancel(); }
                    catch (Exception ex) { ticket.Fail(ex); }
                });
                if (System.Threading.Volatile.Read(ref inlineAttempted) != 0)
                    ticket.RejectExecutor(new InvalidOperationException(
                        "host CPU graph requires queued executor; inline dispatch rejected"));
                else
                    accepted = true;
            }
            catch (Exception ex) { ticket.RejectExecutor(ex); }
            finally
            {
                System.Threading.Volatile.Write(ref admittedState, accepted ? 1 : 2);
                admission.TrySetResult(accepted);
            }
            return ticket;
        }
    }
}
