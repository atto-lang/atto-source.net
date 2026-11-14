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
            if (_actors.Count == 2 || _actors.ContainsKey(name))
                throw Error("duplicate actor or two-stage budget exceeded", at.Offset);
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
            while (Current.Text == "define" && _actors.Count < 2)
                ReadActor();
            if (_actors.Count != 2)
                throw Error("exactly two source-defined actors required");

            Take("pub");
            Expect("fnc");
            Expect("main");
            Expect("(");
            Expect(")");
            Expect(":");
            Expect("int");
            Expect("{");

            var stages = new ActorPipeStageV1[2];
            for (int i = 0; i < stages.Length; i++)
            {
                Token start = Current;
                Expect("let");
                string binding = Name();
                if (i > 0 && stages[0].Binding == binding)
                    throw Error("duplicate actor binding", start.Offset);
                Expect("=");
                Expect("spawn");
                string type = Name();
                if (!_actors.TryGetValue(type, out ActorHandler? handler))
                    throw Error("unknown actor type", start.Offset);
                if (i > 0 && stages[0].ActorType == type)
                    throw Error("actor graph cannot alias or reuse a source actor",
                        start.Offset);
                Expect("(");
                Expect(")");
                Expect(";");
                stages[i] = new ActorPipeStageV1(binding, type, handler.Input,
                    handler.Output, handler.Method, start.Offset);
            }

            string producer = Name();
            Token pipe = Current;
            Expect("pipe");
            string consumer = Name();
            Expect(";");
            if (producer != stages[0].Binding || consumer != stages[1].Binding)
                throw Error("edges must connect owned source then sink exactly once",
                    pipe.Offset);
            if (stages[0].MessageOutput != stages[1].MessageInput)
                throw Error("nominal pipe output/input message type mismatch",
                    pipe.Offset);
            if (stages[0].MessageInput != "int")
                throw Error("v1 input message must be int", pipe.Offset);

            string sentTo = Name();
            Token sendAt = Current;
            Expect(".");
            Expect("send");
            Expect("(");
            Token literal = Current;
            if (literal.Text.Length == 0 || !literal.Text.All(c => IsAsciiDigit(c) || c == '_') ||
                !long.TryParse(literal.Text.Replace("_", "", StringComparison.Ordinal),
                    NumberStyles.None, CultureInfo.InvariantCulture, out long message))
                throw Error("v1 requires one nonnegative checked i64 input literal",
                    literal.Offset);
            _position++;
            Expect(")");
            Expect(";");
            if (sentTo != producer)
                throw Error("direct consumer sends bypass owned graph ingress", sendAt.Offset);

            Expect("stop");
            if (Name() != producer)
                throw Error("producer must stop first");
            Expect(";");
            Expect("stop");
            if (Name() != consumer)
                throw Error("consumer must stop after producer");
            Expect(";");
            Expect("return");
            Expect("0");
            Expect(";");
            Expect("}");
            if (Current.Text.Length != 0)
                throw Error("unexpected declarations or trailing statements");

            ActorHandler a = _actors[stages[0].ActorType];
            ActorHandler b = _actors[stages[1].ActorType];
            // Deliberately a scalar oracle. It proves the guest-authored
            // handler expressions independently; it does not route mailboxes.
            string scalar = $"fnc {a.Method}({a.Input} {a.Parameter}):{a.Output} {a.Body}\n" +
                            $"fnc {b.Method}({b.Input} {b.Parameter}):{b.Output} {b.Body}\n" +
                            (b.Output == "int"
                                ? $"fnc main():int{{return {b.Method}({a.Method}({message.ToString(CultureInfo.InvariantCulture)}));}}"
                                : "fnc main():int{return 0;}");
            try
            {
                _ = OresStructuredCfgLowering.LowerModule(scalar);
            }
            catch (FormatException ex)
            {
                throw Error("scalar handler composition fails checked CFG: " + ex.Message);
            }
            return new ActorPipeGraphV1(Array.AsReadOnly(stages),
                new ActorPipeEdgeV1(producer, consumer, pipe.Offset), message, scalar);
        }
    }
}
