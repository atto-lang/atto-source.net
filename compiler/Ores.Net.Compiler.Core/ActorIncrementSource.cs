using System.Globalization;

namespace Ores.Net.Compiler;

/// <summary>
/// Frozen, source-checked IR for the first synchronous actor-state operation.
/// No arbitrary user statement/expression or delegated scheduler authority.
/// </summary>
public sealed class VerifiedActorIncrement
{
    internal VerifiedActorIncrement(ActorStateSchemaMetadata schema,
        string method, int methodOffset, string field, int fieldIndex, long delta)
    {
        Schema = schema;
        MethodName = method;
        MethodOffset = methodOffset;
        FieldName = field;
        FieldIndex = fieldIndex;
        Delta = delta;
    }
    public ActorStateSchemaMetadata Schema { get; }
    public string MethodName { get; }
    public int MethodOffset { get; }
    public string FieldName { get; }
    public int FieldIndex { get; }
    public long Delta { get; }
}

/// <summary>
/// Strictly bounded first guest actor method grammar. The syntax is parsed by
/// the same lexer as scalar sources. No text regex or substring admission.
/// Only exactly one synchronous state.increment-by-positive-literal callback
/// may enter this experimental IR; every other actor body fails closed.
/// </summary>
public static partial class OresParser
{
    public static VerifiedActorIncrement InspectSynchronousActorIncrement(string source)
    {
        var tokens = Tokenize(source);
        var schema = FindActorStateSchema(tokens) ??
            throw new FormatException("Expected one isolated actor state declaration");
        int p = 0;

        Token At() => tokens[Math.Min(p, tokens.Count - 1)];
        bool Take(string word)
        {
            if (At().Text != word) return false;
            p++;
            return true;
        }
        void Expect(string word)
        {
            if (!Take(word))
                throw new FormatException(
                    $"Actor increment requires '{word}' at offset {At().Offset}");
        }
        string Identifier()
        {
            var text = At().Text;
            if (text.Length == 0 || text[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
                text.Skip(1).Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
                throw new FormatException($"Actor method requires identifier at offset {At().Offset}");
            p++;
            return text;
        }

        Expect("define");
        _ = Take("pub");
        Expect("isolated");
        Expect("actor");
        if (Identifier() != schema.ActorName)
            throw new FormatException("Actor name does not match validated schema");
        Expect("as");
        Expect("state");
        Expect("{");
        // Every field in the state prefix was already strictly validated by
        // FindActorStateSchema. Skip precisely one tokenized declaration block.
        // This method slice never admits nested state-layout statements.
        int consumed = 0;
        while (At().Text != "}")
        {
            if (At().Text.Length == 0 || At().Text == "{" || ++consumed > 128)
                throw new FormatException("Malformed or excessive actor state schema");
            p++;
        }
        Expect("}");
        Expect("pub");
        var methodOffset = At().Offset;
        var methodName = Identifier();
        Expect("(");
        Expect(")");
        Expect(":");
        Expect("int");
        Expect("using");
        Expect("state");
        Expect(".");
        Expect("read");
        Expect(",");
        Expect("state");
        Expect(".");
        Expect("write");
        Expect("{");
        Expect("return");
        Expect("self");
        Expect(".");
        Expect("handle_state");
        Expect("(");
        Expect("critical");
        Expect("|");
        var parameter = Identifier();
        Expect("|");
        Expect("->");
        Expect("{");
        var writeReceiver = Identifier();
        Expect(".");
        var writeField = Identifier();
        Expect("=");
        var readReceiver = Identifier();
        Expect(".");
        var readField = Identifier();
        Expect("+");
        var deltaToken = At();
        if (deltaToken.Text.Length == 0 ||
            deltaToken.Text.Any(c => c is < '0' or > '9' and not '_') ||
            !long.TryParse(deltaToken.Text.Replace("_", "", StringComparison.Ordinal),
                NumberStyles.None, CultureInfo.InvariantCulture, out long delta) ||
            delta != 1)
            throw new FormatException(
                $"Actor increment supports only checked + 1 at offset {deltaToken.Offset}");
        p++;
        Expect(";");
        Expect("return");
        var returnReceiver = Identifier();
        Expect(".");
        var returnField = Identifier();
        Expect(";");
        Expect("}");
        Expect(")");
        Expect(";");
        Expect("}");
        Expect("end");
        if (At().Text.Length != 0)
            throw new FormatException($"Unexpected trailing actor source at offset {At().Offset}");

        if (writeReceiver != parameter || readReceiver != parameter ||
            returnReceiver != parameter || writeField != readField ||
            readField != returnField)
            throw new FormatException("State callback alias or field mismatch");
        var field = schema.Fields.FirstOrDefault(x => x.Name == writeField);
        if (field is null || field.Kind != ActorStateFieldKind.Int64)
            throw new FormatException("Actor increment requires a declared int state field");
        if (methodName == "handle_state")
            throw new FormatException("Reserved actor intrinsic method name");
        return new VerifiedActorIncrement(schema, methodName, methodOffset,
            writeField, field.Index, delta);
    }
}
