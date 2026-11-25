using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Ores.Net.Compiler;

internal sealed class CfgGuestResultEmitter
{
    private readonly MetadataBuilder metadata;
    private readonly TypeReferenceHandle result;
    private readonly TypeReferenceHandle error;
    private readonly TypeReferenceHandle operations;
    private readonly TypeReferenceHandle arithmetic;
    private readonly TypeReferenceHandle overflow;
    private readonly TypeReferenceHandle divideByZero;
    private readonly TypeReferenceHandle exception;
    private readonly TypeReferenceHandle ordinaryThrow;
    private readonly Dictionary<string, MemberReferenceHandle> methods = new(StringComparer.Ordinal);

    internal CfgGuestResultEmitter(MetadataBuilder metadata, AssemblyReferenceHandle systemRuntime)
    {
        this.metadata = metadata;
        var guestRuntime = metadata.AddAssemblyReference(metadata.GetOrAddString("Ores.Net.Runtime"),
            new Version(1, 0, 0, 0), default, default, default, default);
        TypeReferenceHandle Reference(AssemblyReferenceHandle assembly, string space, string name) =>
            metadata.AddTypeReference(assembly, metadata.GetOrAddString(space), metadata.GetOrAddString(name));

        result = Reference(guestRuntime, "Ores.Net.Runtime", "GuestResultI64");
        error = Reference(guestRuntime, "Ores.Net.Runtime", "GuestError");
        operations = Reference(guestRuntime, "Ores.Net.Runtime", "GuestResults");
        arithmetic = Reference(guestRuntime, "Ores.Net.Runtime", "StrictSigned64Arithmetic");
        ordinaryThrow = Reference(guestRuntime, "Ores.Net.Runtime", "GuestThrowSignal");
        overflow = Reference(systemRuntime, "System", "OverflowException");
        divideByZero = Reference(systemRuntime, "System", "DivideByZeroException");
        exception = Reference(systemRuntime, "System", "Exception");
    }

    internal static bool Uses(VerifiedCfgFunction graph) =>
        Uses(graph.ResultType) || graph.Parameters.Any(Uses) || graph.Blocks.Any(block =>
            block.Terminator is CfgSignal || block.Instructions.Any(op =>
                Uses(op.Type) || op.Opcode is >= CfgOpcode.TrapCall and <= CfgOpcode.CheckedModulo));

    private static bool Uses(IrValueType type) => type.IsResult() ||
        type is IrValueType.GuestError or IrValueType.GuestText;

    internal void Encode(SignatureTypeEncoder encoder, IrValueType type)
    {
        if (type.IsResult()) encoder.Type(result, isValueType: true);
        else if (type == IrValueType.GuestError) encoder.Type(error, isValueType: false);
        else if (type == IrValueType.GuestText) encoder.String();
        else throw new FormatException("Unsupported guest Result ABI type");
    }

    private enum Slot { I64, Bool, Text, Result, Error, Exception, ThrowSignal }

    private void Encode(SignatureTypeEncoder encoder, Slot slot)
    {
        switch (slot)
        {
            case Slot.I64: encoder.Int64(); break;
            case Slot.Bool: encoder.Boolean(); break;
            case Slot.Text: encoder.String(); break;
            case Slot.Result: encoder.Type(result, isValueType: true); break;
            case Slot.Error: encoder.Type(error, isValueType: false); break;
            case Slot.Exception: encoder.Type(exception, isValueType: false); break;
            case Slot.ThrowSignal: encoder.Type(ordinaryThrow, isValueType: false); break;
        }
    }

    private MemberReferenceHandle Method(string name, Slot output, params Slot[] inputs)
    {
        if (methods.TryGetValue(name, out var method)) return method;
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature().Parameters(inputs.Length,
            ret => Encode(ret.Type(), output), pros =>
            {
                foreach (var input in inputs) Encode(pros.AddParameter().Type(), input);
            });
        var owner = name is "Rem" or "Mod" ? arithmetic : operations;
        method = metadata.AddMemberReference(owner, metadata.GetOrAddString(name),
            metadata.GetOrAddBlob(signature));
        methods.Add(name, method);
        return method;
    }

    internal bool WriteOperation(InstructionEncoder il, CfgInstruction op)
    {
        if (op.Opcode == CfgOpcode.GuestTextLiteral)
        {
            il.LoadString(metadata.GetOrAddUserString(op.TextOperand!));
            return true;
        }
        if (op.Opcode == CfgOpcode.ResultWiden)
        {
            // The two source types share a CIL value layout; only the
            // verifier can widen the permitted error alternatives.
            il.LoadLocal(op.Left!.Value);
            return true;
        }
        if (op.Opcode is CfgOpcode.GuestErrorEqual or CfgOpcode.GuestErrorNotEqual)
        {
            il.LoadLocal(op.Left!.Value);
            il.LoadLocal(op.Right!.Value);
            il.OpCode(ILOpCode.Ceq);
            if (op.Opcode == CfgOpcode.GuestErrorNotEqual)
            {
                il.LoadConstantI4(0);
                il.OpCode(ILOpCode.Ceq);
            }
            return true;
        }

        var method = op.Opcode switch
        {
            CfgOpcode.GuestErrorLiteral => Method("CreateError", Slot.Error, Slot.Text),
            CfgOpcode.GuestErrorIsType => Method("IsErrorType", Slot.Bool, Slot.Error, Slot.Text),
            CfgOpcode.GuestErrorHasMessage => Method("ErrorHasMessage", Slot.Bool, Slot.Error, Slot.Text),
            CfgOpcode.GuestTextEqual or CfgOpcode.GuestTextNotEqual => Method("TextEquals", Slot.Bool, Slot.Text, Slot.Text),
            CfgOpcode.ResultOk => Method("Ok", Slot.Result, Slot.I64),
            CfgOpcode.ResultOkOption => Method("OkOption", Slot.Result, Slot.Bool, Slot.I64),
            CfgOpcode.ResultErr => Method("Err", Slot.Result, Slot.Error),
            CfgOpcode.ResultErrText => Method("ErrText", Slot.Result, Slot.Text),
            CfgOpcode.ResultIsOk => Method("IsOk", Slot.Bool, Slot.Result),
            CfgOpcode.ResultIsErr => Method("IsErr", Slot.Bool, Slot.Result),
            CfgOpcode.ResultUnwrap or CfgOpcode.ResultUnwrapOptionPayload => Method("Unwrap", Slot.I64, Slot.Result),
            CfgOpcode.ResultUnwrapOptionTag => Method("UnwrapOptionTag", Slot.Bool, Slot.Result),
            CfgOpcode.ResultUnwrapOr => Method("UnwrapOr", Slot.I64, Slot.Result, Slot.I64),
            CfgOpcode.ResultUnwrapErr => Method("UnwrapErr", Slot.Error, Slot.Result),
            CfgOpcode.ResultUnwrapTextErr => Method("UnwrapTextErr", Slot.Text, Slot.Result),
            CfgOpcode.OptionUnwrap => Method("UnwrapOption", Slot.I64, Slot.Bool, Slot.I64),
            CfgOpcode.CheckedRemainder => Method("Rem", Slot.I64, Slot.I64, Slot.I64),
            CfgOpcode.CheckedModulo => Method("Mod", Slot.I64, Slot.I64, Slot.I64),
            _ => default
        };
        if (method.IsNil) return false;
        if (op.Left is int left) il.LoadLocal(left);
        if (op.Right is int right) il.LoadLocal(right);
        if (op.TextOperand is string text) il.LoadString(metadata.GetOrAddUserString(text));
        il.Call(method);
        if (op.Opcode == CfgOpcode.GuestTextNotEqual)
        {
            il.LoadConstantI4(0);
            il.OpCode(ILOpCode.Ceq);
        }
        return true;
    }

    internal void WriteSignal(InstructionEncoder il, CfgSignal signal)
    {
        string name = signal.Effect switch
        {
            CfgGuestEffect.Throw => "Throw",
            CfgGuestEffect.Panic => "Panic",
            CfgGuestEffect.Raise => "Raise",
            _ => throw new FormatException("Unknown guest effect")
        };
        il.LoadLocal(signal.Error);
        il.Call(Method(name, Slot.Exception, Slot.Error));
        il.OpCode(ILOpCode.Throw);
    }

    internal void WriteTrap(InstructionEncoder il, ControlFlowBuilder flow, MethodDefinitionHandle callee,
        IReadOnlyList<int> arguments, int destination)
    {
        // A CFG edge enters outside the protected range. Only evaluation by
        // this generated callee is covered; handlers leave empty-stack.
        il.OpCode(ILOpCode.Nop);
        var start = il.DefineLabel();
        var overflowHandler = il.DefineLabel();
        var divideHandler = il.DefineLabel();
        var throwHandler = il.DefineLabel();
        var finish = il.DefineLabel();

        il.MarkLabel(start);
        foreach (int argument in arguments) il.LoadLocal(argument);
        il.Call(callee);
        Leave();

        il.MarkLabel(overflowHandler);
        il.OpCode(ILOpCode.Pop);
        il.Call(Method("FromOverflow", Slot.Result));
        Leave();

        il.MarkLabel(divideHandler);
        il.OpCode(ILOpCode.Pop);
        il.Call(Method("FromDivisionByZero", Slot.Result));
        Leave();

        il.MarkLabel(throwHandler);
        il.Call(Method("FromThrow", Slot.Result, Slot.ThrowSignal));
        Leave();
        il.MarkLabel(finish);

        flow.AddCatchRegion(start, overflowHandler, overflowHandler, divideHandler, overflow);
        flow.AddCatchRegion(start, overflowHandler, divideHandler, throwHandler, divideByZero);
        flow.AddCatchRegion(start, overflowHandler, throwHandler, finish, ordinaryThrow);

        void Leave()
        {
            il.StoreLocal(destination);
            il.Branch(ILOpCode.Leave, finish);
        }
    }
}
