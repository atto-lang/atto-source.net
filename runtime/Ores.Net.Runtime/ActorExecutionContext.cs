using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ores.Net.Runtime;

/// <summary>
/// Runtime-issued identity of one logical execution, independent of
/// its physical carrier. Future suspension must retain this exact context;
/// unrelated DATA must never acquire its frames. This is local authority,
/// not a serialized message or a CLR ExecutionContext capture.
/// </summary>
[JsonConverter(typeof(ActorExecutionContextNoWireConverter))]
public sealed class ActorExecutionContext
{
    internal ActorExecutionContext(ActorRef actor, ulong executionSequence, ActorFrameStorage storage)
    {
        if (executionSequence == 0) throw new ArgumentOutOfRangeException(nameof(executionSequence));
        Actor = actor;
        ExecutionSequence = executionSequence;
        Stack = new ActorExecutionStack(storage, this);
    }

    public ActorRef Actor { get; }
    public ulong ExecutionSequence { get; }
    internal ActorExecutionStack Stack { get; }
}

public sealed class ActorExecutionContextNoWireConverter : JsonConverter<ActorExecutionContext>
{
    public override ActorExecutionContext Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Actor execution authority cannot be deserialized");

    public override void Write(Utf8JsonWriter writer, ActorExecutionContext value,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("Actor execution authority cannot be serialized");
}
