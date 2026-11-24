using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>Static C# lowering for checked protocol actor bindings.</summary>
internal static class ProtocolActorSourceEmitter
{
    internal static string Emit(IReadOnlyList<VerifiedProtocolActor> actors)
    {
        var code = new StringBuilder();
        code.AppendLine("// Generated from verified Atto protocol actor bindings.");
        code.AppendLine("using Ores.Net.Runtime;");
        code.AppendLine("namespace Oreslang.Generated;");
        foreach (var actor in actors) WriteActor(code, actor);
        return code.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void WriteActor(StringBuilder code, VerifiedProtocolActor actor)
    {
        var protocol = actor.Protocol;
        var states = protocol.Edges.SelectMany(edge => new[] { edge.Source, edge.Target })
            .Append(protocol.InitialState).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var messages = actor.Handlers.OrderBy(handler => handler.Message, StringComparer.Ordinal).ToArray();
        var tags = messages.Select((handler, ordinal) => (handler.Message, ordinal))
            .ToDictionary(entry => entry.Message, entry => entry.ordinal, StringComparer.Ordinal);
        var edges = protocol.Edges.OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Message, StringComparer.Ordinal).ToArray();

        code.Append("public static class Actor_").AppendLine(actor.Name);
        code.AppendLine("{");
        for (int i = 0; i < states.Length; i++)
        {
            code.Append("    public const ushort State_").Append(states[i]).Append(" = ")
                .Append(i.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
        }
        code.AppendLine("    private static readonly ActorProtocolPlan Plan = new(");
        code.Append("        \"").Append(Fingerprint(protocol, states, messages, edges)).Append("\", ")
            .Append(states.Length.ToString(CultureInfo.InvariantCulture)).Append(", State_")
            .Append(protocol.InitialState).AppendLine(",");
        code.AppendLine("        new ProtocolPayloadField[][] {");
        foreach (var handler in messages)
        {
            code.Append("            [");
            code.AppendJoin(", ", handler.Parameters.Select(field =>
                $"new({LongLiteral(field.Type.Range.Minimum)}, {LongLiteral(field.Type.Range.Maximum)})"));
            code.AppendLine("],");
        }
        code.AppendLine("        }, new ProtocolMailboxEdge[] {");
        foreach (var edge in edges)
        {
            if (messages[tags[edge.Message]].Destination != edge.Target)
                throw new InvalidOperationException("Protocol edge differs from the verified handler destination");
            code.Append("            new(State_").Append(edge.Source).Append(", ")
                .Append(tags[edge.Message].ToString(CultureInfo.InvariantCulture)).Append(", State_")
                .Append(edge.Target).AppendLine("),");
        }
        code.AppendLine("        });");
        code.AppendLine("    public static ActorRef SpawnRuntime(ActorRuntime runtime, ActorRef replyTo) =>");
        code.AppendLine("        Plan.Spawn(runtime, replyTo);");
        foreach (var handler in messages)
        {
            code.Append("    public static void Send_").Append(handler.Message)
                .Append("(ActorRuntime runtime, ActorRef actor");
            var arguments = handler.Parameters.Select((_, index) => "arg" +
                index.ToString(CultureInfo.InvariantCulture)).ToArray();
            for (int i = 0; i < arguments.Length; i++)
            {
                code.Append(handler.Parameters[i].Type.I32 ? ", int " : ", long ").Append(arguments[i]);
            }
            code.AppendLine(") =>");
            code.Append("        Plan.Send(runtime, actor, ").Append(tags[handler.Message].ToString(CultureInfo.InvariantCulture))
                .Append(", [").AppendJoin(", ", arguments).AppendLine("]);");
        }
        code.AppendLine("}");
    }

    private static string Fingerprint(VerifiedProtocol protocol, string[] states, ProtocolHandler[] messages,
        ProtocolEdge[] edges)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write("atto.protocol.mailbox.v1");
        writer.Write(protocol.Name);
        writer.Write(protocol.InitialState);
        writer.Write(states.Length);
        foreach (var state in states)
        {
            writer.Write(state);
            writer.Write(protocol.TerminalStates.Contains(state));
        }
        writer.Write(messages.Length);
        foreach (var message in messages)
        {
            writer.Write(message.Message);
            writer.Write(message.Parameters.Count);
            foreach (var field in message.Parameters)
            {
                writer.Write(field.Type.I32);
                writer.Write(field.Type.Range.Minimum);
                writer.Write(field.Type.Range.Maximum);
                field.Type.Unit.WriteCanonical(writer);
            }
        }
        writer.Write(edges.Length);
        foreach (var edge in edges)
        {
            writer.Write(edge.Source);
            writer.Write(edge.Message);
            writer.Write(edge.Target);
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    private static string LongLiteral(long value) => value == long.MinValue ? "long.MinValue" :
        value.ToString(CultureInfo.InvariantCulture) + "L";
}
