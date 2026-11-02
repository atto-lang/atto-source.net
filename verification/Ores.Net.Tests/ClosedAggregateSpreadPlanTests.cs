using Ores.Net.Compiler;

internal static class ClosedAggregateSpreadPlanTests
{
    public static void Run()
    {
        var source = new ClosedAggregateShape(ClosedAggregateKind.ExactStruct,
            [new ClosedAggregateField("foo", "int")]);
        var plan = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ExactStruct);
        plan.AddCheckedSpread(source);
        plan.AddNamed("bar", "string");
        var result = plan.InferShape();
        Check(result.Kind == ClosedAggregateKind.ExactStruct, "exact inferred struct kind");
        Check(result.Fields.Count == 2 && result.Fields[0].Name == "bar" &&
              result.Fields[1].Name == "foo", "canonical field order");
        Check(source.Fields.Count == 1 && source.Fields[0].Type == "int", "source remains immutable");
        Check(plan.SourceElements == 2 && plan.SourceOrderedWrites.Count == 2,
              "one-shot source elements");
        Check(plan.SourceOrderedWrites[0].FromSpread, "spread provenance");

        var overridePlan = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ReadonlyRecord);
        overridePlan.AddNamed("x", "string");
        overridePlan.AddCheckedSpread(new ClosedAggregateShape(
            ClosedAggregateKind.ExactStruct,
            [new ClosedAggregateField("x", "int"), new ClosedAggregateField("y", "bool")]));
        var updated = overridePlan.InferShape();
        Check(updated.Kind == ClosedAggregateKind.ReadonlyRecord, "explicit record construction");
        Check(updated.Fields[0].Name == "x" && updated.Fields[0].Type == "int", "rightmost type");
        Check(overridePlan.SourceOrderedWrites.Count == 3, "overridden evaluation preserved");

        Reject(() => {
            var bad = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ReadonlyRecord);
            bad.AddNamed("x", "int");
            bad.AddCheckedSpread(new ClosedAggregateShape(ClosedAggregateKind.ReadonlyRecord,
                [new ClosedAggregateField("x", "bool")]));
            bad.AddNamed("x", "string");
        }, "AGGREGATE_DUPLICATE_NAMED_FIELD");
        Reject(() => {
            var bad = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ExactStruct);
            bad.AddCheckedSpread(new ClosedAggregateShape(ClosedAggregateKind.ReadonlyRecord,
                [new ClosedAggregateField("nested", "record")]));
        }, "AGGREGATE_UNSUPPORTED_FIELD_TYPE");
        Reject(() => {
            var bad = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ReadonlyRecord);
            bad.AddCheckedSpread(new ClosedAggregateShape(ClosedAggregateKind.ReadonlyRecord,
                [new ClosedAggregateField("x", "int"), new ClosedAggregateField("x", "int")]));
        }, "AGGREGATE_DUPLICATE_SOURCE_FIELD");
        Reject(() => {
            var bad = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ReadonlyRecord);
            for (int i=0; i<65; i++) bad.AddNamed("x" + i, "int");
            _ = bad.InferShape();
        }, "AGGREGATE_FIELD_BUDGET");
        Reject(() => {
            var bad = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ReadonlyRecord);
            bad.AddNamed("\uD800", "int");
        }, "AGGREGATE_INVALID_UTF8_FIELD");

        var unicode = new ClosedAggregateSpreadPlan(ClosedAggregateKind.ReadonlyRecord);
        unicode.AddNamed("\U00010000", "int");
        unicode.AddNamed("\uE000", "int");
        var sorted = unicode.InferShape();
        Check(sorted.Fields[0].Name == "\uE000", "Unicode scalar not UTF-16 lexical order");
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("AttoLang closed aggregate plan: " + label);
    }
    private static void Reject(Action action, string expected)
    {
        try { action(); }
        catch (FormatException error) when (
            error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new Exception("Aggregate plan did not reject: " + expected);
    }
}
