namespace Ores.Net.Compiler;

/// <summary>
/// Metadata-only actor-state admission; not a guest executable actor IR or
/// a source of scheduler credentials. The storage layout is immutable.
/// </summary>
public enum ActorStateFieldKind { Int64, Boolean }

public sealed record ActorStateFieldMetadata(
    int Index,
    string Name,
    ActorStateFieldKind Kind,
    long Int64Default,
    bool BooleanDefault,
    int SourceOffset);

public sealed class ActorStateSchemaMetadata
{
    internal ActorStateSchemaMetadata(string actorName, int actorOffset,
        int stateOffset, List<ActorStateFieldMetadata> fields)
    {
        ActorName = actorName;
        ActorOffset = actorOffset;
        StateOffset = stateOffset;
        // Return no mutable backing list/array to consumers.
        Fields = Array.AsReadOnly(fields.ToArray());
    }

    public string ActorName { get; }
    public int ActorOffset { get; }
    public int StateOffset { get; }
    public IReadOnlyList<ActorStateFieldMetadata> Fields { get; }
}
