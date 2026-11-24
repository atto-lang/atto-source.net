using System.Collections.Frozen;

namespace Ores.Net.Compiler;

// Source-level graph only. It grants no actor or mailbox authority.
internal sealed record VerifiedProtocol(string Name, string InitialState,
    IReadOnlySet<string> TerminalStates, IReadOnlyList<ProtocolEdge> Edges);

internal sealed record ProtocolEdge(string Source, string Target, string Message,
    IReadOnlyList<ProtocolField> Payload);

internal sealed record ProtocolField(string Name, ProtocolScalar Type);

internal sealed record ProtocolScalar(bool I32, VerifiedI64Range Range, QuantityUnit Unit)
{
    internal bool SameType(ProtocolScalar other) =>
        I32 == other.I32 && Range == other.Range && Unit.SameUnit(other.Unit);
}

public static partial class OresParser
{
    private sealed partial class Reader
    {
        private bool AtProtocolDeclaration() => Current.Text == "define" &&
            _index + 1 < tokens.Count && tokens[_index + 1].Text == "protocol";

        private void ExpectProtocol(string token)
        {
            if (!Take(token)) throw new FormatException(
                $"E_PROTOCOL_SYNTAX: expected '{token}' at offset {Current.Offset}");
        }

        private string ReadProtocolName()
        {
            if (Current.Text is "define" or "as" or "end")
                throw new FormatException($"E_PROTOCOL_SYNTAX: expected a protocol identifier at offset {Current.Offset}");
            return ReadVerifiedName();
        }

        private VerifiedProtocol ParseProtocolDeclaration(HashSet<string> protocolNames)
        {
            ExpectProtocol("define");
            ExpectProtocol("protocol");
            if (protocolNames.Count >= 64) throw new FormatException("E_PROTOCOL_LIMIT: too many protocols");
            if (Current.Text is "int" or "i32")
                throw new FormatException("E_PROTOCOL_DUPLICATE: reserved protocol name");
            string name = ReadProtocolName();
            if (refinementAliases.ContainsKey(name) || !protocolNames.Add(name))
                throw new FormatException($"E_PROTOCOL_DUPLICATE: duplicate protocol '{name}'");
            ExpectProtocol("as");

            string? initial = null;
            var states = new HashSet<string>(StringComparer.Ordinal);
            var terminals = new HashSet<string>(StringComparer.Ordinal);
            var edges = new List<ProtocolEdge>();
            var outgoing = new HashSet<(string Source, string Message)>();
            var signatures = new Dictionary<string, IReadOnlyList<ProtocolField>>(StringComparer.Ordinal);
            while (!Take("end"))
            {
                if (Current.Text.Length == 0)
                    throw new FormatException("E_PROTOCOL_SYNTAX: expected a protocol clause or 'end'");
                string first = ReadProtocolName();
                if (Current.Text != "->" && first == "initial")
                {
                    if (initial is not null)
                        throw new FormatException("E_PROTOCOL_INITIAL: duplicate initial state");
                    initial = ReadProtocolName();
                    states.Add(initial);
                }
                else if (Current.Text != "->" && first == "terminal")
                {
                    if (!terminals.Add(ReadProtocolName()))
                        throw new FormatException("E_PROTOCOL_TERMINAL: duplicate terminal state");
                    if (terminals.Count > 128) throw new FormatException("E_PROTOCOL_LIMIT: too many terminal states");
                }
                else
                {
                    if (edges.Count >= 256) throw new FormatException("E_PROTOCOL_LIMIT: too many protocol edges");
                    ExpectProtocol("->");
                    string target = ReadProtocolName();
                    ExpectProtocol("on");
                    string message = ReadProtocolName();
                    states.Add(first);
                    states.Add(target);
                    if (!outgoing.Add((first, message)))
                        throw new FormatException("E_PROTOCOL_DUPLICATE: duplicate source state and message edge");
                    var payload = ParseProtocolPayload();
                    if (signatures.TryGetValue(message, out var signature))
                    {
                        if (signature.Count != payload.Count ||
                            !signature.Zip(payload).All(pair => pair.First.Type.SameType(pair.Second.Type)))
                            throw new FormatException("E_PROTOCOL_PAYLOAD: inconsistent message payload type");
                    }
                    else signatures.Add(message, payload);
                    edges.Add(new ProtocolEdge(first, target, message, payload));
                }
                if (states.Count > 128) throw new FormatException("E_PROTOCOL_LIMIT: too many protocol states");
                ExpectProtocol(";");
            }
            if (initial is null) throw new FormatException("E_PROTOCOL_INITIAL: missing initial state");
            if (!terminals.IsSubsetOf(states))
                throw new FormatException("E_PROTOCOL_TERMINAL: unknown terminal state");
            if (outgoing.Any(edge => terminals.Contains(edge.Source)))
                throw new FormatException("E_PROTOCOL_TERMINAL: terminal state has outgoing edges");
            return new VerifiedProtocol(name, initial, terminals.ToFrozenSet(StringComparer.Ordinal), edges.AsReadOnly());
        }

        private IReadOnlyList<ProtocolField> ParseProtocolPayload()
        {
            if (!Take("(") || Take(")")) return Array.Empty<ProtocolField>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var payload = new List<ProtocolField>();
            do
            {
                if (payload.Count >= 16) throw new FormatException("E_PROTOCOL_LIMIT: too many payload fields");
                if (Current.Text is not ("int" or "i32") && !refinementAliases.ContainsKey(Current.Text))
                    throw new FormatException("E_PROTOCOL_PAYLOAD: unknown scalar payload type");
                var type = ReadRefinedType();
                string name = ReadProtocolName();
                if (!names.Add(name)) throw new FormatException("E_PROTOCOL_PAYLOAD: duplicate payload field name");
                payload.Add(new ProtocolField(name, new ProtocolScalar(type.I32, type.Range, type.Unit)));
            } while (Take(","));
            if (!Take(")")) throw new FormatException("E_PROTOCOL_PAYLOAD: expected ')' after protocol payload");
            return payload.AsReadOnly();
        }
    }
}
