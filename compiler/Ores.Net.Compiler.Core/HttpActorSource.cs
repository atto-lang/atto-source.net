using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ores.Net.Compiler;

public static partial class OresParser
{
    private static readonly HashSet<string> HttpTransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Content-Length", "Content-Type", "Keep-Alive",
        "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    };

    /// <summary>Emits the closed HTTP receive and response continuation profile for static Native AOT linking.</summary>
    public static string EmitHttpActorHandlers(string source)
    {
        var tokens = Tokenize(source);
        var handlers = new List<HttpHandler>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;

        Token Peek() => tokens[position];
        FormatException Deny(string message) =>
            new($"NET_HTTP_ACTOR_UNSUPPORTED: {message} at offset {Peek().Offset}");
        bool Take(string text)
        {
            if (Peek().Text != text) return false;
            position++;
            return true;
        }
        void Expect(string text)
        {
            if (!Take(text)) throw Deny($"expected '{text}'");
        }
        void Sequence(params string[] parts)
        {
            foreach (string part in parts) Expect(part);
        }
        string Identifier()
        {
            string name = Peek().Text;
            if (name.Length is < 1 or > 128 ||
                name[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
                name.Skip(1).Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) ||
                name is "self" or "http" or "await" or "return" or "val" or "let")
                throw Deny("expected a non-reserved identifier");
            position++;
            return name;
        }
        string StringLiteral()
        {
            string literal = Peek().Text;
            if (!literal.StartsWith('"')) throw Deny("expected a double-quoted HTTP literal");
            try
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(literal));
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    throw Deny("invalid HTTP string literal");
                string value = reader.GetString()!;
                position++;
                return value;
            }
            catch (JsonException)
            {
                throw Deny("unsupported HTTP string escape");
            }
        }

        void ReadHeaders(string exchange, List<HttpHeader> headers)
        {
            while (Take(exchange))
            {
                if (headers.Count == 32) throw Deny("at most 32 response header writes per receive");
                Sequence(".", "set_header", "(");
                string name = StringLiteral();
                if (name.Length is < 1 or > 128 ||
                    name.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                        && !"!#$%&'*+-.^_`|~".Contains(c)) || HttpTransportHeaders.Contains(name))
                    throw Deny("invalid or transport-owned HTTP response header");
                Expect(",");
                string? value = null;
                if (Take(exchange))
                {
                    Sequence(".", "request_id");
                }
                else
                {
                    value = StringLiteral();
                    if (value.Length > 8192 - name.Length - 4 || value.Any(c => c is < ' ' or > '~'))
                        throw Deny("invalid or oversized HTTP response header value");
                }
                Sequence(")", ";");
                headers.Add(new HttpHeader(name, value));
            }
        }

        while (Peek().Text.Length != 0)
        {
            if (handlers.Count == 64) throw Deny("at most 64 handlers per source file");
            Expect("define");
            bool isolated = Take("isoactor");
            if (!isolated) Expect("actor");
            string name = Identifier();
            if (!names.Add(name)) throw Deny("duplicate HTTP actor '" + name + "'");
            Expect("as");
            if (!Take("susp") && !Take("suspends")) Take("async");
            Sequence("receive", "(", "ActorMail", "<", "String", ">");
            string mail = Identifier();
            Sequence(")", ":", "void", "{", "val");
            string exchange = Identifier();
            if (exchange == mail) throw Deny("exchange must have a distinct binding");
            Sequence("=", "http", ".", "claim", "(", mail, ".", "value", ")", ";");

            var headers = new List<HttpHeader>();
            ReadHeaders(exchange, headers);
            string? body = null;
            if (Take("val"))
            {
                body = Identifier();
                if (body == exchange || body == mail) throw Deny("body must have a distinct binding");
                Sequence("=", "await", exchange, ".", "body_text", "(", ")", ";");
                ReadHeaders(exchange, headers);
            }
            Sequence("await", exchange, ".", "respond", "(");
            if (!int.TryParse(Peek().Text, NumberStyles.None, CultureInfo.InvariantCulture, out int status) ||
                status is < 200 or > 599)
                throw Deny("HTTP status must be a literal in 200..599");
            position++;
            Expect(",");
            string contentType = StringLiteral();
            if (contentType.Length > 256 || contentType.Any(char.IsControl) ||
                !MediaTypeHeaderValue.TryParse(contentType, out _))
                throw Deny("invalid response content type");
            Expect(",");
            string? response = null;
            if (body is null)
            {
                response = StringLiteral();
                if (Encoding.UTF8.GetByteCount(response) > 65_536) throw Deny("response literal exceeds 64 KiB");
            }
            else Expect(body);
            Sequence(")", ";", "self", ".", "end", "(", ")", ";", "return", ";", "}", "end");
            handlers.Add(new HttpHandler(name, isolated, headers, status, contentType, response));
        }
        if (handlers.Count == 0) throw Deny("expected an HTTP actor definition");

        var output = new StringBuilder("""
            // Source-checked HTTP handlers. Routing and server lifetime are host-owned.
            using Ores.Net.Runtime;
            using Ores.Net.Runtime.Http;

            namespace Oreslang.Generated;

            public static class HttpActors
            {
                public static ActorHttpEndpoint Bind(string name) => name switch
                {
            """).AppendLine();
        foreach (var handler in handlers)
        {
            output.Append("        ").Append(HttpCSharpLiteral(handler.Name)).Append(" => new(ActorKind.")
                .Append(handler.Isolated ? "Private" : "Shared")
                .AppendLine(", static (actor, exchange) =>")
                .AppendLine("        {");
            foreach (var header in handler.Headers)
            {
                output.Append("            exchange.SetResponseHeader(actor, ")
                    .Append(HttpCSharpLiteral(header.Name)).Append(", ")
                    .Append(header.Value is null ? "exchange.RequestId(actor)" : HttpCSharpLiteral(header.Value))
                    .AppendLine(");");
            }
            output.Append("            exchange.Respond(actor, ")
                .Append(handler.Status.ToString(CultureInfo.InvariantCulture)).Append(", ")
                .Append(HttpCSharpLiteral(handler.ContentType)).Append(", ")
                .Append(handler.Response is null ? "exchange.ReadBodyText(actor)" : HttpCSharpLiteral(handler.Response))
                .AppendLine(");")
                .AppendLine("        }) { AfterResponse = static (actor, runtime) => runtime.Stop(actor.Self) },");
        }
        return output.AppendLine("        _ => throw new System.ArgumentException(\"Unknown compiled HTTP actor\", nameof(name))")
            .AppendLine("    };").AppendLine("}").ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private sealed record HttpHeader(string Name, string? Value);
    private sealed record HttpHandler(string Name, bool Isolated, List<HttpHeader> Headers,
        int Status, string ContentType, string? Response);

    // JSON string escaping emits a subset of the C# ordinary string literal grammar.
    private static string HttpCSharpLiteral(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
