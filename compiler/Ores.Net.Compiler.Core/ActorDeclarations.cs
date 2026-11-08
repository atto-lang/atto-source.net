using System.Collections.ObjectModel;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Parsed, non-executable actor declaration AST. It is deliberately separate
/// from StructuredProgram/VerifiedCfgModule: actor declarations are not
/// accepted by the scalar CIL emitter until typed actor IR, ownership/effect
/// verification and static handler emission are available.
/// </summary>
public enum ActorMessageType
{
    I64,
    Bool
}

public sealed record ActorHandlerParameter(string Name, ActorMessageType Type);

public sealed record ActorHandlerDeclaration(
    string Name,
    IReadOnlyList<ActorHandlerParameter> Parameters,
    int ReturnOffset,
    int BodyOffset,
    int BodyLength,
    string BodySource,
    int SourceOffset);

public sealed record ActorDeclaration(
    string Name,
    bool IsPublic,
    IReadOnlyList<ActorHandlerDeclaration> Handlers,
    int SourceOffset);

public sealed record ActorHandlerSignature(
    string Name, IReadOnlyList<ActorMessageType> ParameterTypes);

public sealed record ActorTypeSignature(
    string Name, bool IsPublic, string SignatureFingerprintSha256,
    IReadOnlyList<ActorHandlerSignature> Handlers);

public sealed record ActorDeclarationSignatureTable(
    IReadOnlyList<ActorTypeSignature> Actors);

/// <summary>
/// Framing parser for top-level define actor declarations. It verifies actor
/// names, receiver erasure, bounded scalar message signatures, handler
/// uniqueness and balanced source spans. Handler bodies remain opaque source
/// slices; this API does NOT type-check, lower or execute them.
/// </summary>
public static class OresActorDeclarationParser
{
    private const int MaxUtf8Bytes = 1_048_576;
    private const int MaxTokens = 100_000;
    private const int MaxActors = 256;
    private const int MaxHandlersPerActor = 64;
    private const int MaxParameters = 16;
    private const int MaxNesting = 128;
    private const int MaxIdentifierLength = 128;

    /// <summary>
    /// Creates deterministic signature-only metadata from a parsed actor AST.
    /// Handler bodies are intentionally excluded: this is not semantic
    /// verification, executable Actor IR, or a runtime actor type ID.
    /// </summary>
    public static ActorDeclarationSignatureTable BuildSignatureTable(
        ActorDeclarationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Actors.Count is <= 0 or > MaxActors)
            throw new FormatException("E_ACTOR_SIGNATURE: invalid actor declaration count");

        var actorNames = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ActorTypeSignature>(unit.Actors.Count);
        foreach (ActorDeclaration actor in unit.Actors)
        {
            if (string.IsNullOrWhiteSpace(actor.Name) || !actorNames.Add(actor.Name) ||
                actor.Handlers.Count is <= 0 or > MaxHandlersPerActor)
                throw new FormatException("E_ACTOR_SIGNATURE: malformed or duplicate actor AST");

            var handlerKeys = new HashSet<string>(StringComparer.Ordinal);
            var handlers = new List<ActorHandlerSignature>(actor.Handlers.Count);
            foreach (ActorHandlerDeclaration handler in actor.Handlers)
            {
                if (string.IsNullOrWhiteSpace(handler.Name) ||
                    handler.Parameters.Count > MaxParameters ||
                    handler.BodyOffset < 0 || handler.BodyLength < 0 ||
                    handler.BodyOffset > unit.SourceLength ||
                    handler.BodyLength > unit.SourceLength - handler.BodyOffset)
                    throw new FormatException("E_ACTOR_SIGNATURE: invalid handler AST span");
                string key = handler.Name + "/" +
                    handler.Parameters.Count.ToString(CultureInfo.InvariantCulture);
                if (!handlerKeys.Add(key))
                    throw new FormatException("E_ACTOR_SIGNATURE: duplicate handler value arity");
                var parameterTypes = handler.Parameters.Select(x => x.Type).ToArray();
                if (parameterTypes.Any(x => x is not (ActorMessageType.I64 or ActorMessageType.Bool)))
                    throw new FormatException("E_ACTOR_SIGNATURE: unsupported message parameter type");
                handlers.Add(new ActorHandlerSignature(handler.Name,
                    Array.AsReadOnly(parameterTypes)));
            }

            // Length-prefix every string and include visibility, handler name,
            // value arity and parameter types. Sorting signatures makes the
            // fingerprint independent of source handler declaration order.
            var canonical = new StringBuilder();
            AppendPart(canonical, actor.Name);
            canonical.Append(actor.IsPublic ? "P;" : "I;");
            foreach (var handler in handlers.OrderBy(x => x.Name, StringComparer.Ordinal)
                         .ThenBy(x => x.ParameterTypes.Count))
            {
                AppendPart(canonical, handler.Name);
                canonical.Append(handler.ParameterTypes.Count.ToString(CultureInfo.InvariantCulture));
                canonical.Append(':');
                foreach (ActorMessageType type in handler.ParameterTypes)
                {
                    canonical.Append(type == ActorMessageType.I64 ? 'i' : 'b');
                }
                canonical.Append(';');
            }
            string fingerprint = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())));
            if (!fingerprints.Add(fingerprint))
                throw new FormatException("E_ACTOR_SIGNATURE: duplicate actor signature fingerprint");
            result.Add(new ActorTypeSignature(actor.Name, actor.IsPublic, fingerprint,
                new ReadOnlyCollection<ActorHandlerSignature>(handlers)));
        }
        return new ActorDeclarationSignatureTable(
            new ReadOnlyCollection<ActorTypeSignature>(result));
    }

    private static void AppendPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    private readonly record struct Token(string Text, int Start, int End);

    public static ActorDeclarationUnit Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateInput(source);
        var tokens = Lex(source);
        var reader = new Reader(source, tokens);
        return reader.ParseUnit();
    }

    private static void ValidateInput(string source)
    {
        if (source.IndexOf('\0') >= 0)
            throw new FormatException("E_ACTOR_SOURCE: NUL is not permitted");
        for (int i = 0; i < source.Length; i++)
        {
            if (char.IsHighSurrogate(source[i]))
            {
                if (++i >= source.Length || !char.IsLowSurrogate(source[i]))
                    throw new FormatException("E_ACTOR_SOURCE: malformed UTF-16");
            }
            else if (char.IsLowSurrogate(source[i]))
                throw new FormatException("E_ACTOR_SOURCE: malformed UTF-16");
        }
        if (Encoding.UTF8.GetByteCount(source) > MaxUtf8Bytes)
            throw new FormatException("E_ACTOR_SOURCE: source exceeds 1 MiB");
    }

    private static IReadOnlyList<Token> Lex(string source)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int start = i;
                i += 2;
                int depth = 1;
                while (depth != 0)
                {
                    if (i + 1 >= source.Length)
                        throw new FormatException($"E_ACTOR_SOURCE: unterminated comment at {start}");
                    if (source[i] == '/' && source[i + 1] == '*')
                    {
                        if (++depth > MaxNesting)
                            throw new FormatException($"E_ACTOR_SOURCE: comment nesting exceeds {MaxNesting} at {start}");
                        i += 2;
                    }
                    else if (source[i] == '*' && source[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else i++;
                }
                continue;
            }
            int tokenStart = i;
            if (IsIdentifierStart(c))
            {
                i++;
                while (i < source.Length && IsIdentifierPart(source[i])) i++;
                string word = source[tokenStart..i];
                if (word.Length > MaxIdentifierLength)
                    throw new FormatException($"E_ACTOR_SOURCE: identifier exceeds {MaxIdentifierLength} at {tokenStart}");
                tokens.Add(new Token(word, tokenStart, i));
            }
            else if (c == '"')
            {
                i++;
                bool closed = false;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (source[i++] == '"') { closed = true; break; }
                }
                if (!closed || i > source.Length)
                    throw new FormatException($"E_ACTOR_SOURCE: unterminated string at {tokenStart}");
                tokens.Add(new Token(source[tokenStart..i], tokenStart, i));
            }
            else
            {
                // Handler bodies are opaque in AST v1. Lex punctuation
                // as individual tokens so ordinary operators and delimiters
                // inside the body do not prevent exact source-span capture.
                // The actor declaration grammar below still accepts only its
                // explicit punctuation at the declaration/signature boundary.
                if (!(char.IsPunctuation(c) || char.IsSymbol(c)) || c == '_' || c == '"')
                    throw new FormatException($"E_ACTOR_SOURCE: unsupported character U+{(int)c:X4} at {i}");
                i++;
                tokens.Add(new Token(source[tokenStart..i], tokenStart, i));
            }
            if (tokens.Count > MaxTokens)
                throw new FormatException("E_ACTOR_SOURCE: token budget exceeded");
        }
        tokens.Add(new Token("", source.Length, source.Length));
        return tokens.AsReadOnly();
    }

    private static bool IsIdentifierStart(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    private static bool IsIdentifierPart(char c) =>
        IsIdentifierStart(c) || c is >= '0' and <= '9';

    private sealed class Reader(string source, IReadOnlyList<Token> tokens)
    {
        private int index;
        private Token Current => tokens[index];

        private bool Take(string text)
        {
            if (Current.Text != text) return false;
            index++;
            return true;
        }

        private Token Expect(string text)
        {
            if (Current.Text != text)
                throw Error($"expected '{text}'");
            return tokens[index++];
        }

        private string ReadName(bool allowReceive = false)
        {
            Token token = Current;
            if (token.Text.Length == 0 || !IsIdentifierStart(token.Text[0]) ||
                token.Text is "define" or "pub" or "actor" or "class" or "as" or
                    "self" or "int" or "bool" or "void" or "await" or
                    "suspends" or "critical" ||
                (!allowReceive && token.Text == "receive"))
                throw Error("expected actor/handler identifier");
            index++;
            return token.Text;
        }

        private FormatException Error(string reason) =>
            new($"E_ACTOR_DECLARATION: {reason} at offset {Current.Start}");

        public ActorDeclarationUnit ParseUnit()
        {
            var actors = new List<ActorDeclaration>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (Current.Text.Length != 0)
            {
                int start = Current.Start;
                bool isPublic = Take("pub");
                Expect("define");
                Expect("actor");
                string name = ReadName();
                if (!names.Add(name))
                    throw Error($"duplicate actor declaration '{name}'");
                Expect("as");
                var handlers = new List<ActorHandlerDeclaration>();
                var signatures = new HashSet<string>(StringComparer.Ordinal);
                while (Current.Text != "end")
                {
                    if (Current.Text.Length == 0)
                        throw Error($"actor '{name}' is missing end");
                    if (handlers.Count >= MaxHandlersPerActor)
                        throw Error("actor handler count exceeds limit");
                    var handler = ReadHandler();
                    // The current language's value-arity overload rule and
                    // receiver erasure make this signature identity stable.
                    string signature = handler.Name + "/" + handler.Parameters.Count;
                    if (!signatures.Add(signature))
                        throw Error($"duplicate actor handler signature '{signature}'");
                    handlers.Add(handler);
                }
                Expect("end");
                if (handlers.Count == 0)
                    throw Error($"actor '{name}' must declare at least one handler");
                actors.Add(new ActorDeclaration(name, isPublic,
                    new ReadOnlyCollection<ActorHandlerDeclaration>(handlers), start));
                if (actors.Count > MaxActors)
                    throw Error($"actor declaration count exceeds {MaxActors}");
            }
            if (actors.Count == 0)
                throw Error("source must contain at least one actor declaration");
            return new ActorDeclarationUnit(
                new ReadOnlyCollection<ActorDeclaration>(actors), source.Length);
        }

        private ActorHandlerDeclaration ReadHandler()
        {
            Token nameToken = Current;
            string name = ReadName(allowReceive: true);
            Expect("(");
            var parameters = new List<ActorHandlerParameter>();
            bool receiverSeen = false;
            if (Current.Text != ")")
            {
                do
                {
                    if (parameters.Count >= MaxParameters)
                        throw Error("actor handler parameter count exceeds limit");
                    if (Take("self"))
                    {
                        if (receiverSeen || parameters.Count != 0)
                            throw Error("self receiver must occur once in the first position");
                        receiverSeen = true;
                        continue;
                    }
                    ActorMessageType type = Current.Text switch
                    {
                        "int" => ActorMessageType.I64,
                        "bool" => ActorMessageType.Bool,
                        _ => throw Error("actor message parameter type must be int or bool in declaration AST v1")
                    };
                    index++;
                    string parameterName = ReadName();
                    if (parameters.Any(p => p.Name == parameterName))
                        throw Error($"duplicate actor handler parameter '{parameterName}'");
                    parameters.Add(new ActorHandlerParameter(parameterName, type));
                }
                while (Take(","));
            }
            Expect(")");
            if (parameters.Count > MaxParameters)
                throw Error("actor handler parameter count exceeds limit");
            // Receive signatures are data-only. Function/closure and arbitrary
            // nominal values cannot be sent through mailboxes or pub-sub.
            Expect(":");
            int returnOffset = Current.Start;
            if (!Take("void"))
                throw Error("actor handler declaration AST v1 requires an explicit void result");
            Token open = Expect("{");
            int bodyStart = open.End;
            int depth = 1;
            int bodyEnd = bodyStart;
            while (depth > 0)
            {
                if (Current.Text.Length == 0)
                    throw Error($"handler '{name}' has an unterminated body");
                if (Current.Text is "await" or "suspends" or "critical")
                    throw Error($"{Current.Text} requires actor effect/scheduler lowering");
                if (Current.Text is "define" or "pub")
                    throw Error("nested declarations are not allowed inside actor handler bodies");
                Token t = tokens[index++];
                if (t.Text == "{")
                {
                    if (++depth > MaxNesting)
                        throw new FormatException($"E_ACTOR_DECLARATION: handler nesting exceeds {MaxNesting} at {t.Start}");
                }
                else if (t.Text == "}")
                {
                    depth--;
                    if (depth == 0) bodyEnd = t.Start;
                }
            }
            // Only a balanced opaque body span is recorded. No semantic body
            // validation or code generation is implied by this parsed AST.
            return new ActorHandlerDeclaration(name,
                new ReadOnlyCollection<ActorHandlerParameter>(parameters),
                returnOffset, bodyStart, bodyEnd - bodyStart,
                source[bodyStart..bodyEnd], nameToken.Start);
        }
    }
}
