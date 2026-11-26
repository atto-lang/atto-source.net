using System.Text;

namespace Ores.Net.Compiler;

/// <summary>Closed proof: root main, one capture-free int producer, one observer, one await.</summary>
public sealed class VerifiedTaskI64Program
{
    public VerifiedIrFunction Producer { get; }
    internal VerifiedTaskI64Program(VerifiedIrFunction producer) => Producer = producer;
}

/// <summary>
/// Experimental, explicitly selected source profile. This is not the general
/// Task/Observable binder. No actors, captures, nested tasks or producer await.
/// </summary>
public static class TaskI64Source
{
    public static VerifiedTaskI64Program Check(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Reuse scalar validation (including malformed UTF-16 and byte bounds)
        // on producer expressions below; enforce the whole input budget here.
        if (source.Length > 1_048_576 || Encoding.UTF8.GetByteCount(source) > 1_048_576)
            throw new FormatException("Task-i64 source exceeds 1 MiB");
        if (source.Contains('\0')) throw new FormatException("Task-i64 source contains NUL");
        try { _ = new UTF8Encoding(false, true).GetByteCount(source); }
        catch (EncoderFallbackException error) { throw new FormatException("Task-i64 source contains invalid UTF-16", error); }
        var tokens = Lex(source);
        int at = 0;
        string Current() => at < tokens.Count ? tokens[at] : "";
        void Expect(string token)
        {
            if (Current() != token)
                throw new FormatException($"Task-i64 profile expected '{token}' at token {at}");
            at++;
        }
        if (Current() == "define") at++;
        if (Current() == "pub") at++;
        bool suspendingEntry = Current() is "susp" or "suspends";
        if (suspendingEntry) at++;
        Expect("fnc"); Expect("main"); Expect("("); Expect(")");
        Expect(":"); Expect("int"); Expect("{");
        string bindingKind = Current();
        if (bindingKind is not ("const" or "let" or "val"))
            throw new FormatException("Task-i64 root requires one explicit Future<int> binding");
        at++;
        bool taskFuture = Current() == "TaskFuture";
        if (taskFuture && bindingKind == "const")
            throw new FormatException("E_CONST: TaskFuture is a runtime-only affine handle");
        if (taskFuture) Expect("TaskFuture"); else Expect("Future");
        Expect("<"); Expect("int"); Expect(">");
        string name = Current();
        if (name.Length is < 1 or > 128 || !IdentifierStart(name[0]) ||
            !name.All(IdentifierPart) || name is "task" or "await" or "return" or "susp" or "suspends" or "val" or "sync" or
                "Future" or "TaskFuture" or "Task" or "self" or "main" or "int" or "let" or "const" or
                "throw" or "raise" or "panic" or "async" or "suspend" or "spawn" or
                "actor" or "class" or "if" or "else" or "while" or "for" or "end" or
                "true" or "false" or "fnc" or "pub" or "define" or "rt")
            throw new FormatException("Task-i64 Future binding needs an ordinary identifier");
        at++;
        Expect("=");
        bool canonical = Current() == "task";
        if (canonical && (!suspendingEntry || bindingKind == "const"))
            throw new FormatException("Task-i64 scheduled task requires a suspending root and a runtime Future binding");
        if (taskFuture && !canonical)
            throw new FormatException("E_UNSUPPORTED: TaskFuture requires a directly spawned child in the closed Task-i64 profile");
        if (!canonical)
        {
            Expect("Future"); Expect("."); Expect("from_task"); Expect("(");
        }
        Expect("task");
        if (canonical) { Expect("<"); Expect("int"); Expect(">"); }
        Expect("{");
        if (Current() == "^") at++;
        else Expect("return");
        var body = new List<string>();
        while (Current() != ";")
        {
            if (Current().Length == 0)
                throw new FormatException("Task-i64 producer return is unterminated");
            string token = Current();
            if (!(token.All(c => char.IsAsciiDigit(c) || c == '_') && char.IsAsciiDigit(token[0])) &&
                token is not ("+" or "-" or "*" or "(" or ")"))
                throw new FormatException("Task-i64 producer only supports capture-free checked int arithmetic; captures, effects and suspension require general Task lowering");
            body.Add(token); at++;
        }
        // Existing parser + SSA verifier establish checked i64 arithmetic, AST
        // depth, digit separators, stack/local limits and no ambient captures.
        var expression = OresParser.Parse("fnc main():int{return " + string.Join(' ', body) + ";}");
        var producer = OresIrLowering.Lower(expression);
        if (producer.Instructions.Count > 4096)
            throw new FormatException("Task-i64 producer exceeds 4096 verified operations");
        Expect(";"); Expect("}");
        if (!canonical) Expect(")");
        Expect(";");
        // Only a direct, consuming await is proven for the principal handle.
        // No full task-control API or alias can escape this closed root.
        // Recognize a second observation only in this exact closed shape.
        if (taskFuture && (Current() is "val" or "let"))
        {
            int remaining = tokens.Count - at;
            if (remaining == 14 &&
                tokens[at + 1] == "int" && IdentifierStart(tokens[at + 2][0]) &&
                tokens[at + 2].All(IdentifierPart) && tokens[at + 2] != name &&
                tokens[at + 3] == "=" && tokens[at + 4] == "await" &&
                tokens[at + 5] == name && tokens[at + 6] == ";" &&
                tokens[at + 7] == "return" && tokens[at + 8] == tokens[at + 2] &&
                tokens[at + 9] == "+" && tokens[at + 10] == "await" &&
                tokens[at + 11] == name && tokens[at + 12] == ";" && tokens[at + 13] == "}")
                throw new FormatException("E_TASK_RESULT_CONSUMED: TaskFuture result cannot be observed twice");
        }
        Expect("return");
        if (taskFuture && Current() != "await")
            throw new FormatException("E_UNSUPPORTED: TaskFuture control/projection requires general lowering");
        if (Current() == "await")
        {
            // Existing direct await path: exactly one checked Future<int>.
            at++;
            Expect(name);
        }
        else
        {
            // The only extra admitted spelling is the *unary* std.await
            // stage on the same owned Future. It does not create a task,
            // coerce payloads, or generalize this restricted source profile.
            Expect(name);
            Expect("|>");
            Expect("std");
            Expect(".");
            Expect("await");
        }
        Expect(";"); Expect("}");
        if (at != tokens.Count) throw new FormatException("Task-i64 root has trailing source");
        return new VerifiedTaskI64Program(producer);
    }

    private static List<string> Lex(string source)
    {
        var tokens = new List<string>();
        for (int i = 0; i < source.Length;)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                i += 2;
                while (i < source.Length && source[i] is not ('\n' or '\r' or '\u0085' or '\u2028' or '\u2029')) i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2; int depth = 1;
                while (depth > 0)
                {
                    if (i >= source.Length) throw new FormatException("Unterminated Task-i64 comment");
                    if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
                    { if (++depth > 128) throw new FormatException("Task-i64 comment depth exceeds 128"); i += 2; }
                    else if (i + 1 < source.Length && source[i] == '*' && source[i + 1] == '/')
                    { depth--; i += 2; }
                    else i++;
                }
                continue;
            }
            int start = i++;
            if (c == '|' && i < source.Length && source[i] == '>')
            {
                i++;
                tokens.Add("|>");
                continue;
            }
            if (IdentifierPart(c))
            {
                while (i < source.Length && IdentifierPart(source[i])) i++;
            }
            else if (!"(){}:;=<>.+-*^".Contains(c))
                throw new FormatException($"Unsupported Task-i64 source character at offset {start}");
            if (i - start > 128) throw new FormatException("Task-i64 token exceeds 128 characters");
            tokens.Add(source[start..i]);
            if (tokens.Count > 100_000) throw new FormatException("Task-i64 token budget exceeded");
        }
        return tokens;
    }

    private static bool IdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
