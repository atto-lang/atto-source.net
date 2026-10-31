using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Compiler-minted identity for a deliberately opt-in, scalar-only stage-0
/// singleton plan. Identity is module/declaration text, not a guest capability
/// or a process-global singleton address.
/// </summary>
public sealed record SingletonStage0DeclarationId
{
    internal SingletonStage0DeclarationId(string resolvedModule, string declaration)
    {
        ResolvedModule = resolvedModule;
        Declaration = declaration;
    }

    public string ResolvedModule { get; }
    public string Declaration { get; }
}

/// <summary>
/// A checked scalar initializer plan; it does not publish a singleton or admit
/// a general singleton body into the shipping compiler.
/// </summary>
public sealed class SingletonStage0Plan
{
    internal SingletonStage0Plan(
        SingletonStage0DeclarationId identity, bool isPublic, VerifiedIrFunction initializer)
    {
        Identity = identity;
        IsPublic = isPublic;
        Initializer = initializer;
    }

    public SingletonStage0DeclarationId Identity { get; }
    public bool IsPublic { get; }
    public VerifiedIrFunction Initializer { get; }
}

/// <summary>
/// Test-only explicit compiler path, never called by normal parser/CLI entry
/// points. Supports only: define [pub] singleton Name as end, followed by the
/// ordinary scalar main() source. Its result is not a production initializer.
/// </summary>
public static class SingletonStage0Compiler
{
    private const int MaxSourceBytes = 1_048_576;
    private const int MaxModuleBytes = 4096;
    private const int MaxNameLength = 128;
    private const string SyntaxError = "ORES-SINGLETON-STAGE0-SYNTAX";
    private const string BodyUnsupported = "ORES-SINGLETON-STAGE0-BODY-UNSUPPORTED";
    private const string PrepublicationEscape = "ORES-SINGLETON-PREPUBLICATION-ESCAPE";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SingletonStage0Plan Compile(string source, string resolvedModule)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolvedModule);
        try
        {
            if (StrictUtf8.GetByteCount(source) > MaxSourceBytes)
                throw new FormatException(SyntaxError + ": source byte budget exceeded");
            if (StrictUtf8.GetByteCount(resolvedModule) is 0 or > MaxModuleBytes ||
                resolvedModule[0] == '/' || resolvedModule.Contains('\\') ||
                resolvedModule.Contains('\0') ||
                resolvedModule.Split('/').Any(part => part is "" or "." or ".."))
                throw new FormatException(SyntaxError + ": invalid compiler module identity");
        }
        catch (EncoderFallbackException error)
        {
            throw new FormatException(SyntaxError + ": invalid UTF-16 input", error);
        }

        var reader = new ShellLexer(source);
        var current = reader.Next();
        int beginning = current.Start;
        bool hasDefine = false, isPublic = false;
        while (current.Text is "define" or "pub")
        {
            if (current.Text == "define")
            {
                if (hasDefine) Fail(SyntaxError, "duplicate define modifier", current.Start);
                hasDefine = true;
            }
            else
            {
                if (isPublic) Fail(SyntaxError, "duplicate pub modifier", current.Start);
                isPublic = true;
            }
            current = reader.Next();
        }
        if (!hasDefine || current.Text != "singleton")
            Fail(SyntaxError, "expected explicit define singleton", current.Start);

        current = reader.Next();
        if (!current.IsIdentifier || current.Text.Length > MaxNameLength ||
            current.Text is "define" or "pub" or "as" or "end" or "singleton" or "fnc")
            Fail(SyntaxError, "invalid singleton declaration name", current.Start);
        string name = current.Text;
        current = reader.Next();
        if (current.Text != "as")
            Fail(SyntaxError, "expected as after singleton name", current.Start);

        current = reader.Next();
        bool hasBody = false;
        int bodyStart = current.Start;
        while (current.Text != "end")
        {
            if (current.Text.Length == 0)
                Fail(SyntaxError, "unterminated singleton shell", current.Start);
            hasBody = true;
            if (current.IsIdentifier && current.Text == name)
                Fail(PrepublicationEscape, "receiver referenced before publication", current.Start);
            current = reader.Next();
        }
        if (hasBody)
            Fail(BodyUnsupported, "singleton member/body syntax not yet implemented", bodyStart);

        // The shell is removed only in this explicitly opted-in compiler path.
        // The real scalar parser checks all remaining source and produces a
        // verified i64 IR function; it cannot silently admit a second shell.
        string scalar = string.Concat(source[..beginning], source[current.End..]);
        VerifiedIrFunction initializer = OresIrLowering.Lower(OresParser.Parse(scalar));
        return new SingletonStage0Plan(
            new SingletonStage0DeclarationId(resolvedModule, name), isPublic, initializer);
    }

    private static void Fail(string category, string message, int offset) =>
        throw new FormatException(category + ": " + message + " at offset " + offset);

    private readonly record struct Token(string Text, int Start, int End, bool IsIdentifier);

    private sealed class ShellLexer(string text)
    {
        private int _index;

        public Token Next()
        {
            SkipTrivia();
            int start = _index;
            if (_index == text.Length) return new Token("", start, start, false);
            char first = text[_index++];
            if (!IsLetter(first) && first != '_')
                return new Token(text[start.._index], start, _index, false);
            while (_index < text.Length &&
                   (IsLetter(text[_index]) || text[_index] == '_' ||
                    text[_index] is >= '0' and <= '9'))
                _index++;
            return new Token(text[start.._index], start, _index, true);
        }

        private static bool IsLetter(char value) =>
            value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

        private void SkipTrivia()
        {
            for (;;)
            {
                while (_index < text.Length && char.IsWhiteSpace(text[_index])) _index++;
                if (_index + 1 >= text.Length || text[_index] != '/') return;
                if (text[_index + 1] == '/')
                {
                    _index += 2;
                    while (_index < text.Length &&
                           text[_index] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029'))
                        _index++;
                    continue;
                }
                if (text[_index + 1] != '*') return;
                int commentStart = _index;
                _index += 2;
                int nesting = 1;
                while (nesting > 0)
                {
                    if (_index == text.Length)
                        Fail(SyntaxError, "unterminated block comment", commentStart);
                    if (_index + 1 < text.Length && text[_index] == '/' && text[_index + 1] == '*')
                    {
                        if (++nesting > 128)
                            Fail(SyntaxError, "block comment nesting exceeds 128", commentStart);
                        _index += 2;
                    }
                    else if (_index + 1 < text.Length && text[_index] == '*' && text[_index + 1] == '/')
                    {
                        _index += 2;
                        nesting--;
                    }
                    else _index++;
                }
            }
        }
    }
}
