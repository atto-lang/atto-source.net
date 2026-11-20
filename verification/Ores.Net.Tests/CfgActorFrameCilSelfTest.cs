using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class CfgActorFrameCilSelfTest
{
    public static void Run()
    {
        EmittedCallsAndExceptionCleanupHaveActorFrameAbi();
        BoolBranchesSpillToTypedActorBytes();
        FrameBudgetRejectsBeforeEmittingArtifact();
        HostTypedSpillRestoreAndExceptionalUnwind();
        PoisonedNestedFrameMustPreserveOriginalQuotaFault();
        Console.WriteLine(
            "PASS .NET actor-backed verified CFG CIL metadata and host typed frame tests " +
            "(NOT executed generated .atto actor or native AOT)");
    }

    private static void EmittedCallsAndExceptionCleanupHaveActorFrameAbi()
    {
        const string source =
            "fnc add(int a,int b):int{return a+b;}" +
            "fnc main():int{return add(20,22);}";
        var module = OresStructuredCfgLowering.LowerModule(source);
        string folder = NewTemp();
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitActorFrameLibrary(module, output);
            using var asm = AssemblyDefinition.ReadAssembly(output);
            var generated = asm.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram");
            var main = generated.Methods.Single(m => m.Name == "Run");
            var helper = generated.Methods.Single(m => m.Name == "helper_add");

            Require(main.Parameters.Count == 1 &&
                main.Parameters[0].ParameterType.FullName ==
                    "Ores.Net.Runtime.ActorContext",
                "generated Run accepts the authenticated physical actor context");
            Require(helper.Parameters.Count == 4 &&
                helper.Parameters[0].ParameterType.FullName ==
                    "Ores.Net.Runtime.ActorContext" &&
                helper.Parameters[1].ParameterType.FullName ==
                    "Ores.Net.Runtime.ActorFrameExecution" &&
                helper.Parameters[2].ParameterType.MetadataType == MetadataType.Int64 &&
                helper.Parameters[3].ParameterType.MetadataType == MetadataType.Int64,
                "nested helpers inherit same execution but allocate separate frames");

            CheckFinally(main, isEntry: true);
            CheckFinally(helper, isEntry: false);
            Require(Calls(main, "BeginFrames") == 1 &&
                Calls(main, "PushFrame") == 1 &&
                Calls(main, "WriteFrameI64") >= 3 &&
                Calls(main, "ReadFrameI64") >= 1 &&
                Calls(main, "helper_add") == 1,
                "main actually writes and reads SSA values in actor-owned frame");
            Require(Calls(helper, "BeginFrames") == 0 &&
                Calls(helper, "PushFrame") == 1 &&
                Calls(helper, "WriteFrameI64") >= 3 &&
                Calls(helper, "ReadFrameI64") >= 2,
                "typed helper arguments and checked addition spill to its own frame");
            Require(main.Body.Variables.Count == 3 &&
                helper.Body.Variables.Count == 3,
                "actor SSA values are not allocated as CLR locals");
            Require(helper.Body.Instructions.Any(i => i.OpCode.Code == Code.Add_Ovf),
                "checked add preserves overflow behavior inside EH-protected actor frame");

            var repeat = Path.Combine(folder, "OresGenerated.repeat.dll");
            CilEmitter.EmitActorFrameLibrary(module, repeat);
            Require(File.ReadAllBytes(output).AsSpan()
                .SequenceEqual(File.ReadAllBytes(repeat)),
                "actor-frame generated PE and metadata are reproducible");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void BoolBranchesSpillToTypedActorBytes()
    {
        var module = OresStructuredCfgLowering.LowerModule(
            "fnc main():int{if 2<3{return 42;}else{return 7;}}");
        string folder = NewTemp();
        try
        {
            var output = Path.Combine(folder, "OresGenerated.dll");
            CilEmitter.EmitActorFrameLibrary(module, output);
            using var asm = AssemblyDefinition.ReadAssembly(output);
            var main = asm.MainModule.Types.Single(t =>
                t.FullName == "Oreslang.Generated.OresProgram")
                .Methods.Single(m => m.Name == "Run");
            Require(Calls(main, "WriteFrameBool") >= 1 &&
                Calls(main, "ReadFrameBool") >= 1,
                "bool SSA condition round-trips through typed native frame bytes");
            Require(main.Body.Instructions.Any(i => i.OpCode.Code == Code.Brtrue),
                "CFG true/false branch remains in generated actor-frame CIL");
            CheckFinally(main, isEntry: true);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void FrameBudgetRejectsBeforeEmittingArtifact()
    {
        // 8,193 verified i64 SSA definitions exceed the default actor
        // 64KiB/8 frame budget but stay within OresCfgVerifier.MaxValues.
        var instructions = Enumerable.Range(0, 8193)
            .Select(i => new CfgInstruction(i, CfgOpcode.ConstantI64,
                IrValueType.I64, I64Constant: i)).ToArray();
        var graph = OresCfgVerifier.Verify(new CfgFunction("main", IrValueType.I64,
            "entry", new[] { new CfgBlock("entry",
                Array.Empty<CfgParameter>(), instructions, new CfgReturn(0)) }));
        string folder = NewTemp();
        try
        {
            string output = Path.Combine(folder, "OresGenerated.dll");
            bool rejected = false;
            try { CilEmitter.EmitActorFrameLibrary(graph, output); }
            catch (FormatException ex) when
                (ex.Message.Contains("budget", StringComparison.Ordinal)) { rejected = true; }
            Require(rejected && !File.Exists(output),
                "oversized verified actor frame rejected before CIL artifact creation");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void HostTypedSpillRestoreAndExceptionalUnwind()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 2,
            MessagesPerDispatch = 1,
            ActorPersistentBytes = 256,
            ActorScratchBytes = 128,
            ActorFrameBytes = 128,
            ActorMaxFrames = 4,
            NativeBudgetBytes = 8192,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
                QueueCapacityPerDomain = 2
            }
        });
        using var completed = new ManualResetEventSlim();
        int result = 0;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var execution = context.BeginFrames();
            var parent = context.PushFrame(execution, 32);
            try
            {
                context.WriteFrameI64(execution, parent, 0, long.MinValue);
                context.WriteFrameBool(execution, parent, 8, true);
                Require(context.ReadFrameI64(execution, parent, 0) == long.MinValue &&
                    context.ReadFrameBool(execution, parent, 8),
                    "canonical little-endian i64 and bool are distinct typed slots");

                var child = context.PushFrame(execution, 16);
                try
                {
                    context.WriteFrameI64(execution, child, 0, 42);
                    Require(context.ReadFrameI64(execution, child, 0) == 42,
                        "callee frame has independently authenticated spill data");
                    // An actual CIL throw must traverse its finally region.
                    throw new OverflowException("checked guest call overflow fixture");
                }
                finally { context.PopFrame(execution, child); }
            }
            catch (OverflowException)
            {
                // Compiler-generated CIL emits finally; this host test only
                // validates the runtime primitive's exceptional unwind.
                int firstByte = 0;
                var another = context.PushFrame(execution, 16);
                Span<byte> zero = stackalloc byte[8];
                context.ReadFrame(execution, another, 0, zero);
                Require(zero.SequenceEqual(new byte[8]),
                    "exceptionally popped frame bytes are zeroed before reissue");
                firstByte = zero[0];
                context.PopFrame(execution, another);
                result = firstByte == 0 ? 42 : 0;
            }
            finally
            {
                context.PopFrame(execution, parent);
                context.FinishFrames(execution);
                completed.Set();
            }
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(completed.Wait(TimeSpan.FromSeconds(5)) &&
                result == 42 && runtime.FaultedTurns == 0,
            "typed spilled frame unwind keeps this actor healthy");
        runtime.Stop(actor);
        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 0 && runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(5)),
            "typed exceptional-unwind frame bytes refunded on STOP");
    }

    private static void PoisonedNestedFrameMustPreserveOriginalQuotaFault()
    {
        using var runtime = new ActorRuntime(new ActorRuntimeOptions
        {
            MaxActors = 2,
            ActorPersistentBytes = 256,
            ActorScratchBytes = 128,
            ActorFrameBytes = 32,
            ActorMaxFrames = 4,
            NativeBudgetBytes = 8192,
            Dispatcher = new ActorDispatcherOptions
            {
                PrivateWorkers = 1, SharedWorkers = 1, UntrustedWorkers = 1,
                QueueCapacityPerDomain = 2
            }
        });
        int observedQuota = 0;
        int offCarrierDenied = 0;
        var actor = runtime.Spawn(ActorKind.Private, (context, _) =>
        {
            var execution = context.BeginFrames();
            var outer = context.PushFrame(execution, 16);
            try
            {
                try
                {
                    _ = context.PushFrame(execution, 24);
                    throw new Exception("Expected nested frame admission failure");
                }
                finally
                {
                    context.PopFrameOnUnwind(execution, outer);
                    context.FinishFramesOnUnwind(execution);
                    bool rejected = Task.Run(() =>
                    {
                        try
                        {
                            context.PopFrameOnUnwind(execution, outer);
                            return false;
                        }
                        catch (InvalidOperationException) { return true; }
                    }).GetAwaiter().GetResult();
                    if (rejected) Interlocked.Exchange(ref offCarrierDenied, 1);
                }
            }
            catch (ActorFrameBudgetExceededException)
            {
                Interlocked.Exchange(ref observedQuota, 1);
                throw;
            }
        });
        runtime.Send(actor, new byte[] { 1 });
        Require(SpinWait.SpinUntil(
            () => runtime.LiveActors == 0 && runtime.FaultedTurns == 1 &&
                  runtime.NativeReservedBytes == 0,
            TimeSpan.FromSeconds(5)),
            "poisoned actor stopped and refunded frame/arena native allocation");
        Require(Volatile.Read(ref observedQuota) == 1 &&
                Volatile.Read(ref offCarrierDenied) == 1 &&
                runtime.CompletedMessages == 0,
            "compiler finally cleanup preserved the exact quota exception " +
            "and refused an off-carrier no-op after poison");
    }

    private static void CheckFinally(MethodDefinition method, bool isEntry)
    {
        Require(method.Body.ExceptionHandlers.Count == 1 &&
                method.Body.ExceptionHandlers[0].HandlerType ==
                    ExceptionHandlerType.Finally,
            "every generated actor-frame method has one real CIL finally region");
        Require(method.Body.Instructions.Any(i => i.OpCode.Code == Code.Leave),
            "guest function return leaves the protected region");
        Require(method.Body.Instructions.Any(i => i.OpCode.Code == Code.Endfinally),
            "CIL finally terminates with endfinally");
        Require(Calls(method, "PopFrameOnUnwind") == 1 &&
                Calls(method, "FinishFramesOnUnwind") == (isEntry ? 1 : 0),
            "nested helper unwinds its frame, only entry finishes execution");
    }

    private static int Calls(MethodDefinition method, string name) =>
        method.Body.Instructions.Count(i =>
            (i.OpCode.Code is Code.Call or Code.Callvirt) &&
            i.Operand is MethodReference mr && mr.Name == name);

    private static string NewTemp()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "ores-actor-frame-cil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception("Actor-backed CIL regression: " + scenario);
    }
}
