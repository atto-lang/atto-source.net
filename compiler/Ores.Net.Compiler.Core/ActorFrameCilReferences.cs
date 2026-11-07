using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Ores.Net.Compiler;

/// <summary>
/// Fixed, signature-checked metadata references to the precompiled runtime.
/// No runtime reflection, dynamic binding, expression compilation or JIT.
/// Constructed once per actor-frame assembly before emitting any method body.
/// </summary>
internal sealed class ActorFrameCilReferences
{
    public TypeReferenceHandle Context { get; private init; }
    public TypeReferenceHandle Execution { get; private init; }
    public TypeReferenceHandle Frame { get; private init; }
    public MemberReferenceHandle Begin { get; private init; }
    public MemberReferenceHandle Push { get; private init; }
    public MemberReferenceHandle Pop { get; private init; }
    public MemberReferenceHandle Finish { get; private init; }
    public MemberReferenceHandle WriteI64 { get; private init; }
    public MemberReferenceHandle ReadI64 { get; private init; }
    public MemberReferenceHandle WriteBool { get; private init; }
    public MemberReferenceHandle ReadBool { get; private init; }

    public static ActorFrameCilReferences Create(MetadataBuilder metadata)
    {
        var assembly = metadata.AddAssemblyReference(
            metadata.GetOrAddString("Ores.Net.Runtime"),
            new Version(1, 0, 0, 0), default, default, default, default);
        var context = metadata.AddTypeReference(assembly,
            metadata.GetOrAddString("Ores.Net.Runtime"),
            metadata.GetOrAddString("ActorContext"));
        var execution = metadata.AddTypeReference(assembly,
            metadata.GetOrAddString("Ores.Net.Runtime"),
            metadata.GetOrAddString("ActorFrameExecution"));
        var frame = metadata.AddTypeReference(assembly,
            metadata.GetOrAddString("Ores.Net.Runtime"),
            metadata.GetOrAddString("ActorFrameHandle"));

        MemberReferenceHandle Method(string name, int count,
            Action<ReturnTypeEncoder> result,
            Action<ParametersEncoder> parameters)
        {
            var blob = new BlobBuilder();
            new BlobEncoder(blob).MethodSignature(isInstanceMethod: true)
                .Parameters(count, result, parameters);
            return metadata.AddMemberReference(context,
                metadata.GetOrAddString(name), metadata.GetOrAddBlob(blob));
        }

        // No unrestricted namespace/type discovery: exact known signatures
        // are the only authority generated actor methods may call.
        return new ActorFrameCilReferences
        {
            Context = context,
            Execution = execution,
            Frame = frame,
            Begin = Method("BeginFrames", 0,
                r => r.Type().Type(execution, isValueType: false), _ => { }),
            Push = Method("PushFrame", 2,
                r => r.Type().Type(frame, isValueType: false),
                p =>
                {
                    p.AddParameter().Type().Type(execution, isValueType: false);
                    p.AddParameter().Type().Int32();
                }),
            Pop = Method("PopFrame", 2, r => r.Void(),
                p =>
                {
                    p.AddParameter().Type().Type(execution, isValueType: false);
                    p.AddParameter().Type().Type(frame, isValueType: false);
                }),
            Finish = Method("FinishFrames", 1, r => r.Void(),
                p => p.AddParameter().Type().Type(execution, isValueType: false)),
            WriteI64 = Method("WriteFrameI64", 4, r => r.Void(),
                p =>
                {
                    p.AddParameter().Type().Type(execution, isValueType: false);
                    p.AddParameter().Type().Type(frame, isValueType: false);
                    p.AddParameter().Type().Int32();
                    p.AddParameter().Type().Int64();
                }),
            ReadI64 = Method("ReadFrameI64", 3,
                r => r.Type().Int64(),
                p =>
                {
                    p.AddParameter().Type().Type(execution, isValueType: false);
                    p.AddParameter().Type().Type(frame, isValueType: false);
                    p.AddParameter().Type().Int32();
                }),
            WriteBool = Method("WriteFrameBool", 4, r => r.Void(),
                p =>
                {
                    p.AddParameter().Type().Type(execution, isValueType: false);
                    p.AddParameter().Type().Type(frame, isValueType: false);
                    p.AddParameter().Type().Int32();
                    p.AddParameter().Type().Boolean();
                }),
            ReadBool = Method("ReadFrameBool", 3,
                r => r.Type().Boolean(),
                p =>
                {
                    p.AddParameter().Type().Type(execution, isValueType: false);
                    p.AddParameter().Type().Type(frame, isValueType: false);
                    p.AddParameter().Type().Int32();
                })
        };
    }
}
