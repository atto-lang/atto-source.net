using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Deliberately bounded first source-executed generator slice for the .NET
/// Native-AOT backend. It accepts only canonical zero-argument synchronous
/// <c>gen*</c> declarations yielding signed i64 literals. It does not widen the
/// shipping scalar OresParser, which remains fail-closed for generator syntax.
/// </summary>
public sealed class CompiledGeneratorSourceV1
{
    private readonly long[] _yields;

    internal CompiledGeneratorSourceV1(string name, bool isPublic, long[] yields)
    {
        Name = name;
        IsPublic = isPublic;
        _yields = (long[])yields.Clone();
    }

    public string Name { get; }
    public bool IsPublic { get; }
    public int YieldCount => _yields.Length;
    public long YieldAt(int index) => _yields[index];
    public long[] CopyYields() => (long[])_yields.Clone();
}

public static class OresGeneratorV1Compiler
{
    private const int MaxSourceBytes = 1_048_576;
    private const int MaxYields = 4096;
    private const int MaxCommentDepth = 128;
    private const int MaxIdentifierLength = 128;

    public static CompiledGeneratorSourceV1 Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        var reader = new Reader(source);

        bool isPublic = reader.TakeWord("pub");
        if (reader.TakeWord("async") || reader.TakeWord("suspend") || reader.TakeWord("suspends"))
            throw reader.Error("generator source v1 currently supports synchronous gen* only");

        reader.ExpectWord("gen");
        reader.Expect('*');
        string name = reader.Identifier();
        reader.Expect('(');
        if (!reader.Take(')'))
            throw reader.Error("generator source v1 is zero-argument");
        reader.Expect(':');
        reader.ExpectWord("Iterator");
        reader.Expect('<');
        reader.ExpectWord("int");
        reader.Expect('>');
        reader.Expect('{');

        var yields = new List<long>();
        bool returned = false;
        while (!reader.Take('}'))
        {
            if (reader.End)
                throw reader.Error("unterminated generator body");
            if (returned)
                throw reader.Error("unreachable source after generator return");

            if (reader.TakeWord("yield"))
            {
                if (reader.Take('*'))
                    throw reader.Error("yield* requires delegated iterator lowering");
                long value = reader.SignedInteger();
                reader.Expect(';');
                if (yields.Count == MaxYields)
                    throw reader.Error("generator source v1 exceeds 4096 yielded elements");
                yields.Add(value);
                continue;
            }

            if (reader.TakeWord("return"))
            {
                if (!reader.Take(';'))
                    throw reader.Error("generator return cannot carry a value; use yield");
                returned = true;
                continue;
            }

            throw reader.Error("generator source v1 admits only literal yield statements and bare return");
        }

        reader.ExpectEnd();
        return new CompiledGeneratorSourceV1(name, isPublic, yields.ToArray());
    }

    private static void ValidateSource(string source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\0') throw new FormatException("Source contains NUL byte");
            if (char.IsHighSurrogate(c))
            {
                if (++i >= source.Length || !char.IsLowSurrogate(source[i]))
                    throw new FormatException("Source contains invalid UTF-16");
            }
            else if (char.IsLowSurrogate(c))
                throw new FormatException("Source contains invalid UTF-16");
        }
        if (Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            throw new FormatException("Source exceeds 1 MiB UTF-8 generator compiler limit");
    }

    private sealed class Reader(string source)
    {
        private int _index;

        public bool End
        {
            get
            {
                SkipTrivia();
                return _index == source.Length;
            }
        }

        public bool Take(char value)
        {
            SkipTrivia();
            if (_index >= source.Length || source[_index] != value) return false;
            _index++;
            return true;
        }

        public void Expect(char value)
        {
            if (!Take(value)) throw Error($"expected '{value}'");
        }

        public bool TakeWord(string value)
        {
            SkipTrivia();
            int start = _index;
            if (!ReadWord(out string? actual)) return false;
            if (actual == value) return true;
            _index = start;
            return false;
        }

        public void ExpectWord(string value)
        {
            if (!TakeWord(value)) throw Error($"expected '{value}'");
        }

        public string Identifier()
        {
            SkipTrivia();
            int start = _index;
            if (!ReadWord(out string? value))
                throw Error("identifier expected");
            if (value!.Length > MaxIdentifierLength)
                throw new FormatException($"Identifier exceeds {MaxIdentifierLength} characters at offset {start}");
            if (IsReservedIdentifier(value!))
                throw new FormatException($"Reserved generator identifier '{value}' at offset {start}");
            return value;
        }

        private static bool IsReservedIdentifier(string value) => value is
            "import" or "from" or "as" or "extends" or "implements" or
            "try" or "catch" or "throw" or "raise" or "panic" or "recover" or "finally" or
            "end" or "fi" or "if" or "do" or "else" or "then" or "new" or
            "spawn" or "stop" or "done" or "await" or "rt" or
            "async" or "suspend" or "suspends" or "critical" or
            "gen" or "generator" or "nlex" or "trap" or "actor" or "isoactor" or
            "def" or "fnc" or "routine" or "while" or "for" or "of" or "loop" or
            "block" or "break" or "continue" or "yield" or "super" or "elseif" or "elif" or
            "switch" or "match" or "matches" or "is" or "default" or "eq" or "neq" or
            "nb" or "select" or "readch" or "writech" or "cb" or "shared" or "untrusted" or
            "type" or "types" or "typeof" or "interface" or "trait" or "struct" or "impl" or
            "abstract" or "void" or "static" or "pub" or "private" or "return" or "defer" or
            "val" or "const" or "let" or "mut" or "borrow" or "self" or
            "true" or "false" or "null" or "obj" or "arr" or
            "Iterator" or "AsyncIterator" or "int" or
            "define" or "class" or "enum" or "pattern" or "module" or "contract" or
            "conforms" or "namespace" or "gpu" or "cpu" or "quantum" or "over" or
            "structural" or "bool" or "pure" or "Some" or "None" or "Ok" or "Err" or
            "using" or "requires" or "serial" or "critical-read" or "critical-write" or
            "nb-select";

        public long SignedInteger()
        {
            SkipTrivia();
            int start = _index;
            bool negative = false;
            if (_index < source.Length && source[_index] == '-')
            {
                negative = true;
                _index++;
                // '-' is a token, not part of the integer token. Preserve
                // ordinary Oreslang trivia rules between unary minus and its
                // magnitude, including comments.
                SkipTrivia();
            }
            if (_index >= source.Length || source[_index] is < '0' or > '9')
                throw Error("integer literal expected");

            var digits = new StringBuilder();
            bool previousUnderscore = false;
            while (_index < source.Length)
            {
                char c = source[_index];
                if (c is >= '0' and <= '9')
                {
                    digits.Append(c);
                    previousUnderscore = false;
                    _index++;
                    continue;
                }
                if (c == '_')
                {
                    if (digits.Length == 0 || previousUnderscore ||
                        _index + 1 >= source.Length || source[_index + 1] is < '0' or > '9')
                        throw new FormatException($"Invalid i64 digit separator at offset {_index}");
                    previousUnderscore = true;
                    _index++;
                    continue;
                }
                break;
            }

            if (!ulong.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture,
                    out ulong magnitude))
                throw new FormatException($"Invalid i64 at offset {start}");

            if (!negative)
            {
                if (magnitude > long.MaxValue)
                    throw new FormatException($"Invalid i64 at offset {start}");
                return (long)magnitude;
            }
            if (magnitude == 9_223_372_036_854_775_808UL) return long.MinValue;
            if (magnitude > long.MaxValue)
                throw new FormatException($"Invalid i64 at offset {start}");
            return -(long)magnitude;
        }

        public void ExpectEnd()
        {
            SkipTrivia();
            if (_index != source.Length)
                throw Error("unexpected trailing source");
        }

        public FormatException Error(string message) =>
            new($"{message} at offset {_index}");

        private bool ReadWord(out string? value)
        {
            value = null;
            if (_index >= source.Length ||
                !(source[_index] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
                return false;
            int start = _index++;
            while (_index < source.Length &&
                   source[_index] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                _index++;
            value = source[start.._index];
            return true;
        }

        private void SkipTrivia()
        {
            for (;;)
            {
                while (_index < source.Length && char.IsWhiteSpace(source[_index])) _index++;
                if (_index + 1 >= source.Length || source[_index] != '/') return;

                if (source[_index + 1] == '/')
                {
                    _index += 2;
                    while (_index < source.Length &&
                           source[_index] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029'))
                        _index++;
                    continue;
                }

                if (source[_index + 1] != '*') return;
                int start = _index;
                _index += 2;
                int depth = 1;
                while (depth > 0)
                {
                    if (_index == source.Length)
                        throw new FormatException($"Unterminated block comment at offset {start}");
                    if (_index + 1 < source.Length && source[_index] == '/' && source[_index + 1] == '*')
                    {
                        if (++depth > MaxCommentDepth)
                            throw new FormatException($"Block comment nesting exceeds {MaxCommentDepth} at offset {start}");
                        _index += 2;
                    }
                    else if (_index + 1 < source.Length && source[_index] == '*' && source[_index + 1] == '/')
                    {
                        depth--;
                        _index += 2;
                    }
                    else _index++;
                }
            }
        }
    }
}
