using System.Globalization;

namespace Ores.Net.Compiler;

// Compiler-owned, opt-in source plan. These are immutable syntactic and typed
// facts, NOT live ActorRefs, a scheduler plan, or evidence of NativeAOT actors.
public sealed record ActorPipeStageV1(
    string Binding, string ActorType, string MessageInput,
    string MessageOutput, string HandlerMethod, int SourceOffset);

public sealed record ActorPipeEdgeV1(
    string ProducerBinding, string ConsumerBinding, int PipeOffset);

public sealed record ActorPipeGraphV1(
    IReadOnlyList<ActorPipeStageV1> Stages,
    ActorPipeEdgeV1 Edge,
    long InputMessage,
    string ScalarOracleSource)
{
    public const string Evidence = "TYPED_SCALAR_ORACLE_ONLY_NOT_ACTOR_EXECUTION";
    // Source admission may validate up to sixteen ordered stages. The
    // backwards-compatible Edge property denotes the first edge only.
    public IReadOnlyList<ActorPipeEdgeV1> Edges { get; init; } =
        Array.Empty<ActorPipeEdgeV1>();
}

// Reuse the production structured lexer so nested comments, UTF-8 budget,
// source positions and lexical rejection cannot diverge behind a regex parser.
// The ordinary check/compile entrypoints STILL reject actor source.
public static partial class OresStructuredParser
{
    public static ActorPipeGraphV1 PlanActorPipeV1(string source)
    {
        ValidateSourceInput(source);
        var lexer = new Lexer(source, true);
        var tokens = new List<Token>();
        do
        {
            tokens.Add(lexer.Read());
            if (tokens.Count > MaxTokens)
                throw new FormatException("Actor pipe source token budget exceeded");
        } while (tokens[^1].Text.Length != 0);
        return new ActorPipeSourceReaderV1(tokens, source).Read();
    }

    private sealed class ActorPipeSourceReaderV1(List<Token> tokens, string source)
    {
        private readonly List<Token> _tokens = tokens;
        private readonly Dictionary<string, ActorHandler> _actors =
            new(StringComparer.Ordinal);
        private int _position;

        private sealed record ActorHandler(
            string Name, string Input, string Output, string Parameter,
            string Body, string Method, int At);

        private Token Current => _tokens[_position];
        private Token Previous => _tokens[_position - 1];

        private FormatException Error(string message, int? at = null) =>
            new($"E_ACTOR_PIPE_PLAN_V1: {message} at offset {at ?? Current.Offset}");

        private bool Take(string value)
        {
            if (Current.Text != value) return false;
            _position++;
            return true;
        }

        private void Expect(string value)
        {
            if (!Take(value)) throw Error($"expected '{value}'");
        }

        private string Name()
        {
            string name = Current.Text;
            if (name.Length is < 1 or > 48 ||
                !(IsAsciiAlpha(name[0]) || name[0] == '_') ||
                !name.Skip(1).All(c => IsAsciiAlpha(c) || IsAsciiDigit(c) || c == '_'))
                throw Error("expected bounded ASCII identifier");
            _position++;
            return name;
        }

        private static bool IsAsciiAlpha(char c) =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

        private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

        private string Type()
        {
            string type = Current.Text;
            if (type is not ("int" or "bool"))
                throw Error("only nominal int/bool message domains are admitted");
            _position++;
            return type;
        }

        private string Block()
        {
            Token start = Current;
            Expect("{");
            int depth = 1;
            while (depth != 0)
            {
                if (Current.Text.Length == 0)
                    throw Error("unterminated actor handler body");
                if (Current.Text == "{") depth++;
                else if (Current.Text == "}") depth--;
                if (depth > 128)
                    throw Error("actor handler body nesting exceeds 128");
                _position++;
            }
            return source[start.Offset..(Previous.Offset + 1)];
        }

        private ActorHandler ReadActor()
        {
            Token at = Current;
            Expect("define");
            Expect("actor");
            string name = Name();
            if (_actors.Count >= 16 || _actors.ContainsKey(name))
                throw Error("duplicate actor or sixteen-stage budget exceeded", at.Offset);
            Expect("as");
            Expect("on_message");
            Expect("(");
            string input = Type();
            string parameter = Name();
            Expect(")");
            Expect(":");
            string output = Type();
            string body = Block();
            Expect("end");

            string method = "pipe_stage_" + name + "_on_message";
            string checkedSource = $"fnc {method}({input} {parameter}):{output} {body}\n" +
                                   "fnc main():int{return 0;}\n";
            // Real structured source type checking (including checked i64
            // arithmetic); this is NOT scheduler/actor guest execution.
            try
            {
                _ = OresStructuredCfgLowering.LowerModule(checkedSource);
            }
            catch (FormatException ex)
            {
                throw Error($"actor '{name}' handler fails checked CFG: {ex.Message}",
                    at.Offset);
            }
            var handler = new ActorHandler(name, input, output, parameter,
                body, method, at.Offset);
            _actors.Add(name, handler);
            return handler;
        }

        public ActorPipeGraphV1 Read()
        {
            while (Current.Text == "define")
                ReadActor();
            if (_actors.Count < 2)
                throw Error("at least two source-defined actors required");

            Take("pub");
            Expect("fnc");
            Expect("main");
            Expect("(");
            Expect(")");
            Expect(":");
            Expect("int");
            Expect("{");

            var stages = new List<ActorPipeStageV1>();
            var bindings = new HashSet<string>(StringComparer.Ordinal);
            // A nominal actor type can have multiple independently owned
            // instances. Repeated *bindings*, not repeated nominal types,
            // are forbidden; this is how 15-stage transform chains work.
            while (Current.Text == "let")
            {
                Token start = Current;
                if (stages.Count == 16)
                    throw Error("actor pipe exceeds sixteen-stage source budget", start.Offset);
                Expect("let");
                string binding = Name();
                if (!bindings.Add(binding))
                    throw Error("duplicate actor binding", start.Offset);
                Expect("=");
                Expect("spawn");
                string type = Name();
                if (!_actors.TryGetValue(type, out ActorHandler? handler))
                    throw Error("unknown actor type", start.Offset);
                Expect("(");
                Expect(")");
                Expect(";");
                stages.Add(new ActorPipeStageV1(binding, type, handler.Input,
                    handler.Output, handler.Method, start.Offset));
            }
            if (stages.Count < 2)
                throw Error("actor pipe requires at least two owned stages");

            // The source form is one contiguous, ordered pipeline. The
            // contextual "|" alias is admitted only here, where each bound
            // operand is statically an owned ActorRef.
            var edges = new List<ActorPipeEdgeV1>(stages.Count - 1);
            Token startPipe = Current;
            string first = Name();
            if (first != stages[0].Binding)
                throw Error("pipe must begin at the first owned actor", startPipe.Offset);
            for (int i = 1; i < stages.Count; i++)
            {
                Token pipe = Current;
                if (!Take("pipe") && !Take("|"))
                    throw Error("missing ordered actor pipe edge", pipe.Offset);
                string target = Name();
                if (target != stages[i].Binding)
                    throw Error("pipe must follow declaration order exactly", pipe.Offset);
                if (stages[i - 1].MessageOutput != stages[i].MessageInput)
                    throw Error("nominal pipe output/input message type mismatch", pipe.Offset);
                edges.Add(new ActorPipeEdgeV1(stages[i - 1].Binding,
                    stages[i].Binding, pipe.Offset));
            }
            Expect(";");
            if (stages[0].MessageInput != "int")
                throw Error("v1 input message must be int", startPipe.Offset);

            string sentTo = Name();
            Token sendAt = Current;
            Expect(".");
            Expect("send");
            Expect("(");
            Token literal = Current;
            if (literal.Text.Length == 0 ||
                !literal.Text.All(c => IsAsciiDigit(c) || c == '_') ||
                !long.TryParse(literal.Text.Replace("_", "", StringComparison.Ordinal),
                    NumberStyles.None, CultureInfo.InvariantCulture, out long message))
                throw Error("v1 requires one nonnegative checked i64 input literal",
                    literal.Offset);
            _position++;
            Expect(")");
            Expect(";");
            if (sentTo != first)
                throw Error("direct consumer sends bypass owned graph ingress", sendAt.Offset);

            foreach (var stage in stages)
            {
                Expect("stop");
                if (Name() != stage.Binding)
                    throw Error("actors must stop in forward pipeline order");
                Expect(";");
            }
            Expect("return");
            Expect("0");
            Expect(";");
            Expect("}");
            if (Current.Text.Length != 0)
                throw Error("unexpected declarations or trailing statements");

            // This remains scalar CIL, NOT isolated actor execution. Use
            // sequential typed locals instead of recursive nested calls:
            // a 16-stage valid graph must not exhaust an unrelated scalar
            // expression nesting limit. An SSA local is one stage value.
            var scalarMethods = new System.Text.StringBuilder();
            var scalarBody = new System.Text.StringBuilder();
            var emittedHandlerNames = new HashSet<string>(StringComparer.Ordinal);
            string previousValue = message.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < stages.Count; i++)
            {
                ActorHandler handler = _actors[stages[i].ActorType];
                // Distinct owned actor instances may share their nominal
                // handler type. Emit a definition once, invoke at each stage.
                if (emittedHandlerNames.Add(handler.Method))
                    scalarMethods.Append("fnc ").Append(handler.Method).Append('(')
                        .Append(handler.Input).Append(' ').Append(handler.Parameter)
                        .Append("):").Append(handler.Output).Append(' ')
                        .Append(handler.Body).Append('\n');
                string local = "__pipe_stage_value_" + i.ToString(CultureInfo.InvariantCulture);
                scalarBody.Append("let ").Append(handler.Output).Append(' ')
                    .Append(local).Append(" = ").Append(handler.Method)
                    .Append('(').Append(previousValue).Append(");\n");
                previousValue = local;
            }
            scalarBody.Append(stages[^1].MessageOutput == "int"
                ? "return " + previousValue + ";"
                : "return 0;");
            string scalar = scalarMethods.ToString() +
                "fnc main():int{\n" + scalarBody + "\n}";
            try
            {
                _ = OresStructuredCfgLowering.LowerModule(scalar);
            }
            catch (FormatException ex)
            {
                throw Error("scalar handler composition fails checked CFG: " + ex.Message);
            }
            return new ActorPipeGraphV1(stages.AsReadOnly(), edges[0],
                message, scalar) { Edges = edges.AsReadOnly() };
        }
    }
}
