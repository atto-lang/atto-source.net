using Ores.Net.Compiler;

public static class ExplicitExportSyntaxTests
{
    public static void Run()
    {
        static void Require(bool ok, string message)
        {
            if (!ok) throw new Exception("explicit export syntax: " + message);
        }

        static ExplicitExportDeclaration Parse(string source) =>
            ExplicitExportSyntax.Parse(source);

        static void Reject(string source, string code)
        {
            try
            {
                _ = Parse(source);
            }
            catch (FormatException error)
            {
                if (!error.Message.Contains("[" + code + "]", StringComparison.Ordinal))
                    throw new Exception($"explicit export syntax: expected {code}, got {error.Message}");
                return;
            }
            throw new Exception("explicit export syntax: expected rejection " + code);
        }

        var plain = Parse("export X;");
        Require(plain.Scope == ExplicitExportScope.Public && plain.Kind is null &&
                plain.Items.Count == 1 && plain.Items[0].LocalName == "X",
            "plain untyped export");

        var actor = Parse("export[pkg] actor Counter;");
        Require(actor.Scope == ExplicitExportScope.Package &&
                actor.Kind == ExplicitExportKind.Actor &&
                actor.Items[0].LocalName == "Counter",
            "package actor export");

        var grouped = Parse("export(package) class {X, Y as Z,};");
        Require(grouped.Scope == ExplicitExportScope.Package &&
                grouped.Kind == ExplicitExportKind.Class &&
                grouped.Items.Count == 2 &&
                grouped.Items[1].ExternalName == "Z",
            "group alias/trailing comma");

        var mixed = Parse("export {X, Y as Z, #ready,};");
        Require(mixed.Kind is null && mixed.Items.Count == 3 &&
                mixed.Items[2] is { Symbol: true, LocalName: "#ready" },
            "mixed export group");

        var symbols = Parse("export symbol {#ready, #failed,};");
        Require(symbols.Kind == ExplicitExportKind.Symbol &&
                symbols.Items.All(item => item.Symbol),
            "typed Symbol group");

        Require(Parse("export class {X};").Items.Count == 1, "singleton group");

        var self = Parse("export symbol #ready as #ready;");
        Require(self.Items[0] is { Symbol: true, SelfIdentity: true } &&
                self.Items[0].LocalName == self.Items[0].ExternalName,
            "Symbol self identity");

        Reject("export X", "EXPORT_SEMICOLON_REQUIRED");
        Reject("export {};", "EXPORT_EMPTY_GROUP");
        Reject("export class {};", "EXPORT_EMPTY_GROUP");
        Reject("export actor {,X};", "EXPORT_MALFORMED_LIST");
        Reject("export actor {X,,Y};", "EXPORT_MALFORMED_LIST");
        Reject("export actor {X Y};", "EXPORT_MALFORMED_LIST");
        Reject("export actor {{X}};", "EXPORT_NESTED_GROUP");
        Reject("export *;", "EXPORT_WILDCARD_UNSUPPORTED");
        Reject("export * from \"./dep\";", "EXPORT_REEXPORT_UNSUPPORTED");
        Reject("export from \"./dep\" {X};", "EXPORT_REEXPORT_UNSUPPORTED");
        Reject("export dep.X;", "EXPORT_QUALIFIED_TARGET_UNSUPPORTED");
        Reject("export dep::X;", "EXPORT_QUALIFIED_TARGET_UNSUPPORTED");
        Reject("export Foo<int>;", "EXPORT_GENERIC_TARGET_UNSUPPORTED");
        Reject("export foo(int);", "EXPORT_CALLABLE_SIGNATURE_UNSUPPORTED");
        Reject("export {class X, actor Y};", "EXPORT_KIND_PREFIX_IN_GROUP");
        Reject("export actor {actor X};", "EXPORT_KIND_PREFIX_IN_GROUP");
        Reject("export symbol #ready as Ready;", "EXPORT_SYMBOL_BINDING_ALIAS_UNSUPPORTED");
        Reject("export symbol #ready as #ok;", "EXPORT_SYMBOL_IDENTITY_MISMATCH");
        Reject("export[pkg][file] X;", "EXPORT_MULTIPLE_SCOPES");
        Reject("export[project] X;", "EXPORT_UNKNOWN_SCOPE");
        Reject("export symbol ready as #ready;", "EXPORT_SYMBOL_IDENTITY_TARGET_REQUIRES_SYMBOL_SOURCE");
        Reject("export string ready as 'ready';", "EXPORT_UNKNOWN_DECLARATION_KIND");
        Reject("export const ready as 'ready';", "EXPORT_LITERAL_ALIAS_UNSUPPORTED");
        Reject("export class Foo as \"foo\";", "EXPORT_LITERAL_ALIAS_UNSUPPORTED");

        // Comments are lexical trivia, including nested block comments.
        var commented = Parse("/* outer /* nested */ */ export /*x*/ class X;");
        Require(commented.Kind == ExplicitExportKind.Class, "comments changed grammar");

        Console.WriteLine("explicit export syntax: 31 shared vectors + comment-trivia hardening");
    }
}
