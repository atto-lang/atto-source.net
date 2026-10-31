using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// AOT-safe, build-time C# backend for one verified synchronous actor method.
/// Unlike the scalar metadata CIL emitter, this produces C# source that must
/// be compiled into a static NativeAOT build. No runtime emit/reflection.
/// </summary>
public static class ActorIncrementSourceEmitter
{
    public static string EmitCSharp(VerifiedActorIncrement method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var schema = method.Schema;
        var layoutIdentity = ActorStateLayoutPlan.FromSchema(schema).LayoutIdentity;
        if (method.Delta != 1 ||
            method.FieldIndex < 0 || method.FieldIndex >= schema.Fields.Count ||
            schema.Fields[method.FieldIndex].Name != method.FieldName ||
            schema.Fields[method.FieldIndex].Kind != ActorStateFieldKind.Int64)
            throw new InvalidOperationException("Unverified actor increment IR");
        var b = new StringBuilder();
        b.AppendLine("// Generated from verified isolated actor IR; no runtime code generation.");
        b.AppendLine("using Ores.Net.Runtime;");
        b.AppendLine("namespace Oreslang.Generated;");
        b.Append("public static class Actor_").Append(schema.ActorName).AppendLine();
        b.AppendLine("{");
        b.Append("    public const string ActorName = \"").Append(schema.ActorName)
            .AppendLine("\";");
        b.Append("    public const string ActorLayoutIdentity = \"")
            .Append(layoutIdentity).AppendLine("\";");
        b.AppendLine("    public static OresVmHost.ActorTicket Create(OresVmHost host,");
        b.AppendLine("        OresVmHost.IsolateHandle isolate, ulong actorId)");
        b.AppendLine("    {");
        b.AppendLine("        var actor = host.RegisterActor(isolate, actorId);");
        b.AppendLine("        try {");
        b.AppendLine("            host.ConfigureNamedActorState(");
        b.AppendLine("                isolate, actor, ActorName, ActorLayoutIdentity,");
        b.AppendLine("                new OresVmHost.ActorStateField[] {");
        foreach (var field in schema.Fields)
        {
            if (field.Kind == ActorStateFieldKind.Int64)
            {
                var value = field.Int64Default == long.MinValue
                    ? "long.MinValue"
                    : field.Int64Default.ToString(CultureInfo.InvariantCulture) + "L";
                b.Append("                    new(OresVmHost.ActorStateScalarKind.Int64, Int64Default: ")
                    .Append(value).AppendLine("),");
            }
            else if (field.Kind == ActorStateFieldKind.Boolean)
            {
                b.Append("                    new(OresVmHost.ActorStateScalarKind.Boolean, BooleanDefault: ")
                    .Append(field.BooleanDefault ? "true" : "false").AppendLine("),");
            }
            else throw new InvalidOperationException("Unsupported generated actor field kind");
        }
        b.AppendLine("                });");
        b.AppendLine("            return actor;");
        b.AppendLine("        }");
        b.AppendLine("        catch { host.RetireActor(isolate, actor); throw; }");
        b.AppendLine("    }");
        b.Append("    public static long Call_").Append(method.MethodName)
            .AppendLine("(OresVmHost.ActorDispatchScope scope)");
        b.AppendLine("    {");
        b.Append("        return scope.AddInt64(")
            .Append(method.FieldIndex.ToString(CultureInfo.InvariantCulture))
            .AppendLine(", 1L);");
        b.AppendLine("    }");
        b.Append("    public static long Dispatch_").Append(method.MethodName).AppendLine("(");
        b.AppendLine("        OresVmHost.ActorScheduler scheduler, OresVmHost.IsolateHandle isolate,");
        b.AppendLine("        OresVmHost.ActorTicket actor)");
        b.AppendLine("    {");
        b.Append("        return scheduler.DispatchNext(isolate, actor, ActorLayoutIdentity, Call_")
            .Append(method.MethodName).AppendLine(");");
        b.AppendLine("    }");
        b.AppendLine("}");
        return b.ToString();
    }
}
