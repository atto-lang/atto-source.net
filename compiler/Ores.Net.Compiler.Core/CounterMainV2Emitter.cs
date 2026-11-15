using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Verified source-dependent native root runner. The only host bridge is a
/// bounded mailbox output Channel; source awaits suspend the root Task and
/// NEVER park an actor carrier. No reflection, JIT, dynamic compilation, or
/// precomputed stdout is used.
/// </summary>
public static class CounterMainV2Emitter
{
    public static string EmitCSharp(VerifiedCounterMainV2 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var stateMir = ActorStateMirV3.Lower(plan);
        if (!stateMir.ReadLoanReleasedBeforeMailboxSend())
            throw new InvalidOperationException("ACTOR_MIR_V3: mailbox publication during state loan");
        if (plan.Messages.Count != 4 ||
            plan.ActorName.Length is < 1 or > 128 ||
            plan.ActorName[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
            plan.ActorName.Skip(1).Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            throw new InvalidOperationException("Unverified Counter main actor identity/arity");
        var b = new StringBuilder();
        b.AppendLine("// Source-checked Counter v2 async main, static NativeAOT.");
        b.AppendLine("using System;");
        b.AppendLine("using System.Buffers.Binary;");
        b.AppendLine("using System.Globalization;");
        b.AppendLine("using System.Text;");
        b.AppendLine("using System.Threading;");
        b.AppendLine("using System.Threading.Channels;");
        b.AppendLine("using System.Threading.Tasks;");
        b.AppendLine("using Ores.Net.Runtime;");
        b.AppendLine("namespace Oreslang.Generated;");
        b.AppendLine("public static class CounterMain_V2");
        b.AppendLine("{");
        b.AppendLine("    public static async Task<string> RunAsync()");
        b.AppendLine("    {");
        b.AppendLine("        using var runtime = new ActorRuntime(new ActorRuntimeOptions {");
        b.AppendLine("            MaxActors = 4, ActorPersistentBytes = 256, ActorScratchBytes = 64,");
        b.AppendLine("            ActorStackBytes = 16, NativeBudgetBytes = 64 * 1024");
        b.AppendLine("        });");
        b.AppendLine("        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));");
        b.AppendLine("        var outputs = Channel.CreateBounded<long>(new BoundedChannelOptions(4) {");
        b.AppendLine("            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait");
        b.AppendLine("        });");
        b.AppendLine("        ActorRef worker = default;");
        b.AppendLine("        ActorRef observer = runtime.Spawn(ActorKind.Shared, (_, payload) => {");
        b.AppendLine("            if (payload.Length != 16)");
        b.AppendLine("                throw new InvalidOperationException(\"Malformed actor output\");");
        b.AppendLine("            if (BinaryPrimitives.ReadUInt64LittleEndian(payload.Span) != worker.Id)");
        b.AppendLine("                throw new InvalidOperationException(\"Foreign actor output\");");
        b.AppendLine("            long value = BinaryPrimitives.ReadInt64LittleEndian(payload.Span[8..]);");
        b.AppendLine("            if (!outputs.Writer.TryWrite(value))");
        b.AppendLine("                throw new InvalidOperationException(\"Bounded output overflow\");");
        b.AppendLine("        });");
        b.Append("        worker = DeclaredActor_").Append(stateMir.ActorName)
            .AppendLine(".SpawnRuntime(runtime, observer);");
        b.AppendLine("        // get_ready is resolved by synchronous actor registration in this closed profile.");
        b.AppendLine("        Task done = runtime.ObserveTermination(worker);");
        b.AppendLine("        var mail = new byte[8];");
        foreach (long value in stateMir.Messages)
        {
            string sourceLiteral = value == long.MinValue
                ? "long.MinValue" : value.ToString(CultureInfo.InvariantCulture) + "L";
            b.Append("        BinaryPrimitives.WriteInt64LittleEndian(mail, ")
                .Append(sourceLiteral).AppendLine(");");
            b.AppendLine("        runtime.Send(worker, mail);");
        }
        for (int i = 0; i < stateMir.Messages.Count; i++)
            b.Append("        long v").Append(i.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" = await outputs.Reader.ReadAsync(timeout.Token);");
        b.AppendLine("        await done.WaitAsync(timeout.Token);");
        b.AppendLine("        var stdout = new StringBuilder();");
        for (int i = 0; i < stateMir.Messages.Count; i++)
        {
            if (i > 0) b.AppendLine("        stdout.Append('|');");
            b.Append("        stdout.Append(v").Append(i.ToString(CultureInfo.InvariantCulture))
                .AppendLine(".ToString(CultureInfo.InvariantCulture));");
        }
        b.AppendLine("        runtime.Dispose();");
        b.AppendLine("        if (runtime.NativeReservedBytes != 0)");
        b.AppendLine("            throw new InvalidOperationException(\"Actor-private native bytes leaked\");");
        b.AppendLine("        return stdout.ToString();");
        b.AppendLine("    }");
        b.AppendLine("}");
        return b.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
