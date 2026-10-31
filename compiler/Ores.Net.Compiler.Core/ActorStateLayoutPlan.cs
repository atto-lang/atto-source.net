using Ores.Net.Runtime;

namespace Ores.Net.Compiler;

/// <summary>
/// Typed schema-only initialization IR for one isolated actor declaration.
/// Cannot lower guest methods, mint a scheduler ticket, or grant state access.
/// </summary>
public sealed class ActorStateLayoutPlan
{
    public sealed record Field(int Index, string Name, ActorStateFieldKind Kind,
        long Int64Default, bool BooleanDefault, int SourceOffset);

    private ActorStateLayoutPlan(ActorStateSchemaMetadata schema)
    {
        ActorName = schema.ActorName;
        ActorOffset = schema.ActorOffset;
        StateOffset = schema.StateOffset;
        if (schema.Fields.Count is < 1 or > 16)
            throw new InvalidOperationException("unbounded actor state layout");
        var fields = new Field[schema.Fields.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < fields.Length; i++)
        {
            var f = schema.Fields[i];
            if (f.Index != i || !names.Add(f.Name) ||
                string.IsNullOrEmpty(f.Name) || f.SourceOffset < 0)
                throw new InvalidOperationException("invalid actor state field layout");
            if (f.Kind != ActorStateFieldKind.Int64 &&
                f.Kind != ActorStateFieldKind.Boolean)
                throw new InvalidOperationException("unknown actor state field kind");
            if ((f.Kind == ActorStateFieldKind.Int64 && f.BooleanDefault) ||
                (f.Kind == ActorStateFieldKind.Boolean && f.Int64Default != 0))
                throw new InvalidOperationException("actor state field default kind mismatch");
            fields[i] = new Field(f.Index, f.Name, f.Kind,
                f.Int64Default, f.BooleanDefault, f.SourceOffset);
        }
        Fields = Array.AsReadOnly(fields);
    }

    public string ActorName { get; }
    public int ActorOffset { get; }
    public int StateOffset { get; }
    public IReadOnlyList<Field> Fields { get; }

    public static ActorStateLayoutPlan FromSchema(ActorStateSchemaMetadata schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new ActorStateLayoutPlan(schema);
    }

    /// <summary>Fresh host-field copy, not an editable compiler IR view.</summary>
    public OresVmHost.ActorStateField[] ToHostFields()
    {
        var fields = new OresVmHost.ActorStateField[Fields.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            var f = Fields[i];
            fields[i] = f.Kind switch {
                ActorStateFieldKind.Int64 => new(
                    OresVmHost.ActorStateScalarKind.Int64,
                    Int64Default: f.Int64Default),
                ActorStateFieldKind.Boolean => new(
                    OresVmHost.ActorStateScalarKind.Boolean,
                    BooleanDefault: f.BooleanDefault),
                _ => throw new InvalidOperationException("unsupported state field kind")
            };
        }
        return fields;
    }

    /// <summary>
    /// Trusted host bootstrap for a registered actor, not emitted guest CIL
    /// or a `handle_state` code-generation path. Runtime validates identity.
    /// </summary>
    public void Install(OresVmHost host, OresVmHost.IsolateHandle isolate,
        OresVmHost.ActorTicket actor)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.ConfigureNamedActorState(isolate, actor, ActorName, ToHostFields());
    }
}
