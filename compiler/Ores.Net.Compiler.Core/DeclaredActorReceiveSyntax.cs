namespace Ores.Net.Compiler;

/// <summary>
/// Closed, lexer-derived grammar for the seven-file Counter candidate's external
/// runtime ingress. The data carries no dispatch authority; manifest binding,
/// actor-owned state layout, logical turns and NativeAOT execution are separate.
/// </summary>
public sealed record DeclaredActorReceiveSyntax(
    string Namespace, string ActorName, string ReceiverName, string MailName,
    string StateField, string SnapshotName, long StopOnMailValue)
{
    public const string EvidenceScope = "PARSED_SOURCE_ONLY_NOT_EXECUTED";
}

public static partial class OresParser
{
    public static DeclaredActorReceiveSyntax InspectDeclaredActorReceive(string source)
    {
        var tokens = Tokenize(source); // Bounded, comment-aware source lexer.
        int p = 0;
        Token At() => tokens[Math.Min(p, tokens.Count - 1)];
        bool Take(string text)
        {
            if (At().Text != text) return false;
            ++p;
            return true;
        }
        void Expect(string text)
        {
            if (!Take(text))
                throw new FormatException(
                    $"DECLARED_ACTOR_RECEIVE_REJECTED: expected '{text}' at offset {At().Offset}");
        }
        string Identifier()
        {
            string name = At().Text;
            if (name.Length == 0 ||
                name[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
                name.Skip(1).Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z'
                    or >= '0' and <= '9' or '_')) ||
                name is "self" or "return" or "critical" or "val" or "let")
                throw new FormatException(
                    $"DECLARED_ACTOR_RECEIVE_REJECTED: invalid identifier at offset {At().Offset}");
            ++p;
            return name;
        }
        void Critical(string mode)
        {
            int offset = At().Offset;
            Expect("critical");
            if (At().Offset != offset + 8) throw new FormatException(
                "DECLARED_ACTOR_RECEIVE_REJECTED: critical mode must be adjacent");
            Expect("-");
            if (At().Offset != offset + 9) throw new FormatException(
                "DECLARED_ACTOR_RECEIVE_REJECTED: critical mode must be adjacent");
            Expect(mode);
        }
        void StateIntrinsic()
        {
            Expect("self"); Expect("."); Expect("handle_state"); Expect("(");
        }

        Expect("namespace");
        string ns = Identifier();
        Expect(";");
        Expect("external"); Expect("mtd"); Expect("(");
        string actor = Identifier();
        string receiver = Identifier();
        Expect(")"); Expect("receive"); Expect("(");
        Expect("ActorMail"); Expect("<"); Expect("int"); Expect(">");
        string mail = Identifier();
        Expect(")"); Expect(":"); Expect("void"); Expect("{");
        StateIntrinsic();
        Critical("write"); Expect("|");
        string writeView = Identifier();
        Expect("|"); Expect("->"); Expect("{");
        string leftView = Identifier(); Expect("."); string field = Identifier();
        Expect("=");
        string rightView = Identifier(); Expect("."); string rightField = Identifier();
        Expect("+");
        string input = Identifier(); Expect("."); Expect("value");
        Expect(";"); Expect("}"); Expect(")"); Expect(";");

        Expect("val"); Expect("int");
        string snapshot = Identifier();
        Expect("=");
        StateIntrinsic();
        Critical("read"); Expect("|");
        string readView = Identifier();
        Expect("|"); Expect("->");
        string readOwner = Identifier(); Expect("."); string readField = Identifier();
        Expect(")"); Expect(";");

        // Read view is no longer live when the mailbox admission occurs.
        Expect("self"); Expect("."); Expect("send"); Expect("(");
        string published = Identifier();
        Expect(")"); Expect(";");

        Expect("if");
        string stopMail = Identifier(); Expect("."); Expect("value");
        int firstEqual = At().Offset;
        Expect("=");
        if (At().Offset != firstEqual + 1) throw new FormatException(
            "DECLARED_ACTOR_RECEIVE_REJECTED: expected '=='");
        Expect("=");
        string stopToken = At().Text;
        if (stopToken.Length == 0 ||
            !stopToken.All(c => c is >= '0' and <= '9' or '_') ||
            !long.TryParse(stopToken.Replace("_", "", StringComparison.Ordinal),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long stopOn))
            throw new FormatException(
                "DECLARED_ACTOR_RECEIVE_REJECTED: stop value must be signed i64 literal");
        ++p;
        Expect(";"); Expect("do");
        Expect("self"); Expect("."); Expect("end"); Expect("("); Expect(")");
        Expect(";"); Expect("fi");
        Expect("return"); Expect(";"); Expect("}");
        if (At().Text.Length != 0) throw new FormatException(
            "DECLARED_ACTOR_RECEIVE_REJECTED: trailing unverified statements");

        if (receiver == mail || receiver == writeView || receiver == readView ||
            mail == writeView || mail == readView || snapshot == mail ||
            snapshot == receiver || snapshot == writeView || snapshot == readView ||
            writeView == "self" || readView == "self" ||
            writeView != leftView || leftView != rightView ||
            rightField != field || field != readField ||
            readView != readOwner || input != mail ||
            stopMail != mail || published != snapshot)
            throw new FormatException(
                "DECLARED_ACTOR_RECEIVE_REJECTED: state loan alias/owned mail/snapshot mismatch");
        return new DeclaredActorReceiveSyntax(
            ns, actor, receiver, mail, field, snapshot, stopOn);
    }
}
