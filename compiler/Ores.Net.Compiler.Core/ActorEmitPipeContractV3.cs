using System.Text;

namespace Ores.Net.Compiler;

// Compiler-owned typed-emit admission for the *new* AttoLang actor-pipe syntax.
// The existing opt-in ActorPipeSourcePlanV1 and shipping CIL admission remain
// fail-closed. This code emits no actor CIL and creates no runtime ActorRefs.
//
// Typed emit values must originate from an actual receive-body checker,
// including nested if/loop paths; an input type is NEVER an output inference.
// There is no JavaScript/CLR scalar-call substitute for mailbox forwarding.
public sealed record ActorEmitStageV3
{
    public string InstanceId { get; }
    public string ActorType { get; }
    public string ReceiveType { get; }
    public IReadOnlyList<string> EmitTypes { get; }
    public bool Supervisor { get; }

    public ActorEmitStageV3(string instanceId, string actorType, string receiveType,
        IEnumerable<string> emitTypes, bool supervisor = false)
    {
        InstanceId = instanceId;
        ActorType = actorType;
        ReceiveType = receiveType;
        // Freeze all compiler-provided typed observations before any routing
        // validation. Never retain a mutable list from the parser/type checker.
        EmitTypes = Array.AsReadOnly((emitTypes ??
            throw new ArgumentNullException(nameof(emitTypes))).ToArray());
        Supervisor = supervisor;
    }
}

public sealed record ActorEmitLinkV3(string Source, string Target, string PayloadType);

public static class ActorEmitPipeContractV3
{
    public const int MaxStages = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static FormatException Reject(string code, string message) =>
        new($"{code}: {message}");

    private static void RequireIdentity(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0'))
            throw Reject("E_ACTOR_PIPE_IDENTITY",
                "actor type and instance IDs must be nonempty and NUL-free");
        int bytes;
        try { bytes = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException)
        {
            throw Reject("E_ACTOR_PIPE_IDENTITY", "actor identifiers require strict UTF-8");
        }
        if (bytes > 256)
            throw Reject("E_ACTOR_PIPE_IDENTITY", "actor identifier exceeds 256 UTF-8 bytes");
    }

    private static void RequireScalar(string? type)
    {
        if (type is not ("int" or "bool"))
            throw Reject("E_ACTOR_PIPE_PAYLOAD_DOMAIN",
                "v3 ActorMail/emit requires nominal int or bool");
    }

    // Both Java/Graal and LLVM use the same diagnostic codes and source order.
    // Repeated nominal actor types are permitted for distinct instance IDs.
    // The terminal sink has NO emit and may not dynamically invent an egress.
    public static IReadOnlyList<ActorEmitLinkV3> Validate(
        IReadOnlyList<ActorEmitStageV3> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Count is < 2 or > MaxStages)
            throw Reject("E_ACTOR_PIPE_LIMIT", "pipeline requires 2..16 distinct actor instances");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<ActorEmitLinkV3>(stages.Count - 1);
        string? previousOutput = null;
        string? previousId = null;
        for (int i = 0; i < stages.Count; i++)
        {
            ActorEmitStageV3 stage = stages[i] ??
                throw Reject("E_ACTOR_PIPE_IDENTITY", "pipeline cannot contain a missing actor");
            RequireIdentity(stage.InstanceId);
            RequireIdentity(stage.ActorType);
            RequireScalar(stage.ReceiveType);
            if (!seen.Add(stage.InstanceId))
                throw Reject("E_ACTOR_PIPE_DUPLICATE_INSTANCE",
                    "actor instance was attached more than once");
            if (stage.Supervisor)
                throw Reject("E_ACTOR_PIPE_SUPERVISOR",
                    "CONTROL supervisor cannot be a DATA stage");

            if (stage.EmitTypes is null)
                throw Reject("E_ACTOR_PIPE_PAYLOAD_DOMAIN", "missing typed receive emission list");
            string? output = null;
            foreach (string type in stage.EmitTypes)
            {
                RequireScalar(type);
                if (output is not null && output != type)
                    throw Reject("E_ACTOR_PIPE_EMIT_TYPE",
                        "actor has incompatible emitted value types");
                output = type;
            }
            if (i > 0)
            {
                if (previousOutput != stage.ReceiveType)
                    throw Reject("E_ACTOR_PIPE_EMIT_TYPE",
                        $"previous stage emits {previousOutput} but {stage.InstanceId} receives {stage.ReceiveType}");
                links.Add(new ActorEmitLinkV3(previousId!, stage.InstanceId, previousOutput!));
            }
            if (i + 1 == stages.Count)
            {
                if (output is not null)
                    throw Reject("E_ACTOR_PIPE_SINK_EMIT",
                        "terminal sink cannot emit without downstream");
            }
            else if (output is null)
                throw Reject("E_ACTOR_PIPE_STAGE_NO_EMIT",
                    "nonterminal stage must emit a typed value");
            previousOutput = output;
            previousId = stage.InstanceId;
        }
        return Array.AsReadOnly(links.ToArray());
    }
}
