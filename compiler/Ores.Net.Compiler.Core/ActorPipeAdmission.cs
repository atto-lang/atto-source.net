namespace Ores.Net.Compiler;

// Compiler-only admission gate. The native host's actor mailboxes are not
// source-level ActorGraph lowering, and must never be used as a silent fallback.
internal static class ActorPipeAdmission
{
    internal const string Diagnostic = "E_ACTOR_PIPE_BACKEND_UNSUPPORTED";

    // 'pipe' is contextual in AttoLang: a user function, local or field named
    // pipe is not itself an actor graph. Inputs are already lexed (including
    // nested-comment removal), so no raw-source scans or regexes are needed.
    internal static bool IsReservedForm(
        string threeBack, string twoBack, string left, string current, string right)
    {
        if (current is "pipe" or "connect")
        {
            // The Java compiler reserves Actor.pipe/connect; LLVM also
            // compiles ActorUtils.pipe and ActorUtils::pipe. Do not let these
            // names silently fall through into dynamic or scalar invocation.
            bool actorNamespace = twoBack == "Actor" ||
                                  (twoBack == "ActorUtils" && current == "pipe");
            bool staticNamespace = threeBack == "Actor" ||
                                   (threeBack == "ActorUtils" && current == "pipe");
            bool actorFactory = (left == "." && actorNamespace) ||
                                (left == "::" && actorNamespace) || // "::" lexed as one token
                                (left == ":" && twoBack == ":" && staticNamespace);
            if (actorFactory && (right is "(" or "<"))
                return true;
        }

        // Distinct from functional '|>': this is the infix actor-link keyword.
        // A syntactically expression-like pair must not become a scalar call
        // or an implicitly unbounded host-only pipeline.
        return current == "pipe" && EndsExpression(left) && StartsExpression(right);
    }

    internal static FormatException Unsupported(int offset) => new(
        $"{Diagnostic}: actor 'pipe' requires a typed ActorGraph, owned ActorRefs, " +
        "supervisor-bound startup, bounded mailbox admission, atomic link " +
        $"publication and Native AOT guest lowering at offset {offset}");

    private static bool NameOrNumber(string text) =>
        text.Length != 0 && (char.IsAsciiLetterOrDigit(text[0]) || text[0] == '_');

    private static bool EndsExpression(string text) =>
        text is ")" or "]" ||
        (NameOrNumber(text) &&
         text is not ("define" or "pub" or "fnc" or "let" or "val" or "const" or
                      "final" or "return" or "if" or "else" or "then" or "fi" or
                      "while" or "for" or "do" or "as" or "end" or "spawn" or
                      "actor" or "class" or "transform" or "pipe" or "connect"));

    private static bool StartsExpression(string text) =>
        text == "(" ||
        (NameOrNumber(text) &&
         text is not ("define" or "pub" or "fnc" or "let" or "val" or "const" or
                      "final" or "return" or "if" or "else" or "then" or "fi" or
                      "while" or "for" or "do" or "as" or "end" or "actor" or
                      "class" or "transform" or "pipe" or "connect"));
}
