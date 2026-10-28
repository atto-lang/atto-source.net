using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

public enum ManagedSourceTarget
{
    CSharp,
    FSharp
}

/// <summary>
/// Build-time source emitter for the already verified scalar OresIR subset.
/// No dynamic compilation, guest execution, or unsupported syntax admission.
/// The existing CIL emitter remains the Native AOT release path.
/// </summary>
public static class ManagedSourceEmitter
{
    private const int MaxInstructions = 16_384;

    public static string Emit(VerifiedIrFunction program, ManagedSourceTarget target)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (target is not ManagedSourceTarget.CSharp and not ManagedSourceTarget.FSharp)
            throw new FormatException("Unsupported managed source target");
        if (program.Name != "main" || program.ResultType != IrValueType.I64)
            throw new FormatException("Managed source emission requires verified main():i64");
        if (program.Instructions.Count is 0 or > MaxInstructions)
            throw new FormatException("Managed source instruction budget exceeded");

        bool cs = target == ManagedSourceTarget.CSharp;
        var output = new StringBuilder();
        output.AppendLine("// Generated from verified OresIR; do not edit.");
        if (cs)
        {
            output.AppendLine("namespace Oreslang.Generated;");
            output.AppendLine();
            output.AppendLine("public static class OresProgram");
            output.AppendLine("{");
            output.AppendLine("    public static long Run()");
            output.AppendLine("    {");
        }
        else
        {
            output.AppendLine("namespace Oreslang.Generated");
            output.AppendLine();
            // Default F# arithmetic is unchecked; this import is essential
            // for signed i64 parity with CIL add.ovf/sub.ovf/mul.ovf.
            output.AppendLine("open Microsoft.FSharp.Core.Operators.Checked");
            output.AppendLine();
            output.AppendLine("type OresProgram private () =");
            output.AppendLine("    static member Run() : int64 =");
        }

        for (int i = 0; i < program.Instructions.Count; i++)
        {
            var op = program.Instructions[i];
            if (op.ResultId != i)
                throw new FormatException("Managed source requires contiguous verified SSA IDs");

            string expr;
            if (op.Opcode == IrOpcode.ConstantI64 && op.Constant is long number &&
                !op.Left.HasValue && !op.Right.HasValue)
            {
                expr = number == long.MinValue
                    ? (cs ? "long.MinValue" : "System.Int64.MinValue")
                    : number.ToString(CultureInfo.InvariantCulture) + "L";
            }
            else if (op.Opcode is IrOpcode.CheckedAdd or IrOpcode.CheckedSubtract or
                     IrOpcode.CheckedMultiply or IrOpcode.CheckedDivide &&
                     op.Constant is null &&
                     op.Left is int left && op.Right is int right &&
                     left >= 0 && left < i && right >= 0 && right < i)
            {
                string sign = op.Opcode switch
                {
                    IrOpcode.CheckedAdd => "+",
                    IrOpcode.CheckedSubtract => "-",
                    IrOpcode.CheckedMultiply => "*",
                    IrOpcode.CheckedDivide => "/",
                    _ => throw new FormatException("Unsupported verified IR opcode")
                };
                expr = $"v{left} {sign} v{right}";
                if (cs) expr = $"checked({expr})";
            }
            else
            {
                throw new FormatException("Unsupported or malformed verified scalar OresIR");
            }

            if (cs)
                output.Append("        long v").Append(i).Append(" = ")
                    .Append(expr).AppendLine(";");
            else
                output.Append("        let v").Append(i).Append(" : int64 = ")
                    .AppendLine(expr);
        }

        if (program.ReturnId < 0 || program.ReturnId >= program.Instructions.Count)
            throw new FormatException("Invalid verified return ID");
        if (cs)
        {
            output.Append("        return v").Append(program.ReturnId).AppendLine(";");
            output.AppendLine("    }");
            output.AppendLine("}");
        }
        else
            output.Append("        v").Append(program.ReturnId).AppendLine();

        return output.ToString();
    }
}
