using Ores.Net.Compiler;

internal static class ActorStateSchemaAdmissionTests
{
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception("actor metadata: " + label);
    }

    private static void Reject(string source, string expected, string label)
    {
        try { _ = OresParser.InspectActorStateSchema(source); }
        catch (FormatException e) when (e.Message.Contains(expected, StringComparison.Ordinal))
        { return; }
        throw new Exception("actor metadata must reject: " + label);
    }

    public static void Run()
    {
        const string source =
            "// define actor Fake as state { fake: int = 7; } end\n" +
            "define isolated actor Counter as\n" +
            "  state { count: int = -9_223_372_036_854_775_808; " +
            "enabled: bool = true; inactive: bool = false; }\n" +
            "  pub read(): int { return self.handle_state(critical |s| -> s.count); }\n" +
            "end";
        var schema = OresParser.InspectActorStateSchema(source);
        Require(schema.ActorName == "Counter", "nominal actor name");
        Require(schema.ActorOffset == source.IndexOf("actor Counter", StringComparison.Ordinal),
            "actor declaration offset");
        Require(schema.StateOffset == source.IndexOf("state { count:", StringComparison.Ordinal),
            "state declaration offset");
        Require(schema.Fields.Count == 3, "field count");
        var count = schema.Fields[0];
        Require(count.Index == 0 && count.Name == "count" &&
                count.Kind == ActorStateFieldKind.Int64 &&
                count.Int64Default == long.MinValue && !count.BooleanDefault &&
                count.SourceOffset == source.IndexOf("count: int", StringComparison.Ordinal),
            "canonical i64 min and ordered field index");
        var enabled = schema.Fields[1];
        var inactive = schema.Fields[2];
        Require(enabled.Index == 1 && enabled.Name == "enabled" &&
                enabled.Kind == ActorStateFieldKind.Boolean && enabled.BooleanDefault &&
                enabled.Int64Default == 0,
            "enabled boolean default");
        Require(inactive.Index == 2 && inactive.Name == "inactive" &&
                inactive.Kind == ActorStateFieldKind.Boolean && !inactive.BooleanDefault,
            "false default and stable field order");

        // Metadata must never leak a mutable field list to a compiler caller.
        var mutable = schema.Fields as IList<ActorStateFieldMetadata>;
        Require(mutable is not null, "read-only fields implement IList");
        bool refused = false;
        try { mutable![0] = new ActorStateFieldMetadata(
            0, "forged", ActorStateFieldKind.Int64, 42, false, 0); }
        catch (NotSupportedException) { refused = true; }
        Require(refused && schema.Fields[0].Name == "count",
            "metadata cannot be modified after validation");

        // Inspecting metadata must NOT make executable actor lowering available.
        bool blocked = false;
        try { _ = OresParser.Parse(source); }
        catch (FormatException e) when (
            e.Message.Contains("generation-fenced logical-turn lowering is unavailable",
                StringComparison.Ordinal)) { blocked = true; }
        Require(blocked, "normal source parsing still fails closed");

        var max = OresParser.InspectActorStateSchema(
            "define isolated actor MaxI64 as state { limit: int = 9_223_372_036_854_775_807; } end");
        Require(max.Fields.Count == 1 &&
                max.Fields[0].Int64Default == long.MaxValue,
            "canonical i64 maximum default");

        var sixteen = string.Join(" ", Enumerable.Range(0, 16)
            .Select(i => $"f{i}: int = {i};"));
        var full = OresParser.InspectActorStateSchema(
            "define isolated actor Max as state { " + sixteen + " } end");
        Require(full.Fields.Count == 16 && full.Fields[15].Index == 15 &&
                full.Fields[15].Int64Default == 15, "bounded 16-field schema");
        Reject("define isolated actor Max as state { " +
                sixteen + " overflow: bool = true; } end",
            "field limit exceeded", "17th field");
        Reject("define isolated actor Counter as state { x: int = 0; x: bool = false; } end",
            "Duplicate actor state field", "duplicate name");
        Reject("define isolated actor Counter as state { x: float = 0; } end",
            "only int/bool", "unsupported type");
        Reject("define isolated actor Counter as state { x: int = 9223372036854775808; } end",
            "overflows signed i64", "positive overflow");
        Reject("define isolated actor Counter as state { x: int = -9223372036854775809; } end",
            "overflows signed i64", "negative overflow");
        Reject("define isolated actor Counter as state { x: bool = 0; } end",
            "must be true or false", "incorrect boolean default");
        Reject("define isolated actor Counter as state { } end",
            "requires at least one field", "empty schema");
        Reject("define actor Counter as state { x: int = 0; } end",
            "scheduler-owned logical continuation frames", "non-isolated actor");
        Reject("define isolated actor A as state { x: int = 0; } end " +
                "define isolated actor B as state { y: bool = true; } end",
            "Multiple actor declarations", "ambiguous two-actor schema");
        Reject("fnc main():int{return 1;}", "canonical isolated actor",
            "missing actor declaration");
    }
}
