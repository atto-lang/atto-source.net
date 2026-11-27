using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

public sealed record StructuredGuestError(int At, string Message)
    : StructuredExpr(At, IrValueType.GuestError);
public sealed record StructuredGuestText(int At, string Text)
    : StructuredExpr(At, IrValueType.GuestText);
public sealed record StructuredResult(int At, StructuredExpr Value, bool Success, IrValueType ResultType)
    : StructuredExpr(At, ResultType);
public sealed record StructuredTrap(int At, StructuredExpr Operand, IrValueType ResultType)
    : StructuredExpr(At, ResultType);
public sealed record StructuredResultWiden(int At, StructuredExpr Operand)
    : StructuredExpr(At, IrValueType.ResultI64TextOrError);
public sealed record StructuredResultInspect(int At, StructuredExpr Result, CfgOpcode Operation,
    IrValueType ValueType, StructuredExpr? Fallback = null) : StructuredExpr(At, ValueType);
public sealed record StructuredOptionUnwrap(int At, StructuredExpr Option)
    : StructuredExpr(At, IrValueType.I64);
public sealed record StructuredPropagate(int At, StructuredExpr Operand, IrValueType PayloadType)
    : StructuredExpr(At, PayloadType);
public sealed record StructuredErrorTest(int At, StructuredExpr Error, string Name, bool IsType)
    : StructuredExpr(At, IrValueType.Bool);
public sealed record StructuredSignal(int At, StructuredExpr Error, CfgGuestEffect Effect)
    : StructuredStmt(At);

internal static class GuestResultTypes
{
    internal static bool IsResult(this IrValueType type) =>
        type is IrValueType.ResultI64 or IrValueType.ResultOptionI64 or
            IrValueType.ResultI64Text or IrValueType.ResultI64TextOrError;

    internal static bool HasError(this IrValueType type) =>
        type is IrValueType.ResultI64 or IrValueType.ResultOptionI64 or IrValueType.ResultI64TextOrError;

    internal static bool HasTextError(this IrValueType type) =>
        type is IrValueType.ResultI64Text or IrValueType.ResultI64TextOrError;

    internal static IrValueType Payload(this IrValueType type) => type switch
    {
        IrValueType.ResultI64 or IrValueType.ResultI64Text or IrValueType.ResultI64TextOrError => IrValueType.I64,
        IrValueType.ResultOptionI64 => IrValueType.OptionI64,
        _ => throw new FormatException("Expected a closed Result type")
    };

    internal static IrValueType Wrap(IrValueType type) => type switch
    {
        IrValueType.I64 => IrValueType.ResultI64,
        IrValueType.OptionI64 => IrValueType.ResultOptionI64,
        IrValueType.ResultI64Text => IrValueType.ResultI64TextOrError,
        IrValueType.ResultI64 or IrValueType.ResultOptionI64 or IrValueType.ResultI64TextOrError => type,
        _ => throw new FormatException("E_TRAP_PAYLOAD_UNSUPPORTED: this backend requires int or Option<int> success values")
    };
}

public static partial class OresStructuredParser
{
    private sealed partial class Reader
    {
        private const string TrappedBodyPrefix = "_atto_trapped_body_";
        private IrValueType? expectedExpressionType;
        private int trapOperandDepth;
        private int guardFailureDepth;

        private StructuredFunction WrapTrappedCallable(StructuredFunction declaration, int at)
        {
            string rawName = linking?.CanonicalName(TrappedBodyPrefix + functionOrder.Count)
                ?? TrappedBodyPrefix + functionOrder.Count;
            var raw = declaration with { Name = rawName };
            functions.Add(rawName, raw);
            functionOrder.Add(rawName);
            var operands = declaration.Parameters.Select(parameter => (StructuredExpr)
                new StructuredRead(at, parameter.Name, parameter.Type)).ToArray();
            var call = new StructuredCall(at, rawName, Array.AsReadOnly(operands), raw.ResultType);
            var result = new StructuredTrap(at, call, GuestResultTypes.Wrap(raw.ResultType));
            return declaration with { ResultType = result.Type, ReturnValue = result,
                Body = new[] { new StructuredReturn(at, result) } };
        }

        private StructuredExpr ExpectedExpr(int depth, IrValueType type)
        {
            var previous = expectedExpressionType;
            expectedExpressionType = type;
            try { return Expr(depth); }
            finally { expectedExpressionType = previous; }
        }

        private StructuredExpr TypedNode(StructuredExpr expression, params StructuredExpr[] children)
        {
            int height = 1 + children.Select(Height).DefaultIfEmpty(0).Max();
            if (height > MaxDepth) throw Error("Structured AST depth exceeds limit");
            expressionHeights.Add(expression, height);
            return expression;
        }

        private StructuredExpr TrapOperand(int at, int depth)
        {
            // Closed lexical slice: explicit ordinary throw becomes an
            // Err edge in the same caller CFG. Do not outline or copy state.
            if (trapOperandDepth == 0 && inlineTaskDepth == 0 &&
                directInlineWritebackOffset == at && AtSyncTask())
            {
                index += 2;
                trapOperandDepth++;
                try
                {
                    var task = ReadInlineTask(depth + 1, imperative: false, writeBack: true);
                    if (task.Type != IrValueType.I64)
                        throw Error("E_TRAP_INLINE_TASK_UNSUPPORTED: lexical trap slice requires int");
                    var body = TrapInlineStatements(task.Body);
                    return new StructuredSyncTaskValue(at,
                        task with { Body = body, Type = IrValueType.ResultI64 });
                }
                finally { trapOperandDepth--; }
            }
            trapOperandDepth++;
            StructuredExpr operand;
            try { operand = Unary(depth + 1); }
            finally { trapOperandDepth--; }
            return TypedNode(new StructuredTrap(at, operand, GuestResultTypes.Wrap(operand.Type)), operand);
        }

        // Admit only source operations proven not to raise an ordinary
        // error, except the explicit throw that is converted to Err.
        // This closed whitelist excludes checked arithmetic, calls,
        // unwrap, suspension, loops, nested tasks and cleanup until their
        // exceptional edges carry the current lexical environment.
        private static bool TrapInlinePure(StructuredExpr value) => value switch
        {
            StructuredInt or StructuredBool => true,
            StructuredRead read => read.Type is IrValueType.I64 or IrValueType.Bool,
            StructuredBinary binary when binary.Operator is
                (StructuredOperator.EqualI64 or StructuredOperator.NotEqualI64 or
                 StructuredOperator.LessThanI64 or StructuredOperator.GreaterThanI64 or
                 StructuredOperator.LessThanOrEqualI64 or StructuredOperator.GreaterThanOrEqualI64 or
                 StructuredOperator.EqualBool or StructuredOperator.NotEqualBool or
                 StructuredOperator.LogicalAnd or StructuredOperator.LogicalOr or
                 StructuredOperator.LogicalXor) =>
                    TrapInlinePure(binary.Left) && TrapInlinePure(binary.Right),
            _ => false
        };

        private IReadOnlyList<StructuredStmt> TrapInlineStatements(IReadOnlyList<StructuredStmt> statements)
        {
            var body = new List<StructuredStmt>(statements.Count);
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case StructuredLet declaration when TrapInlinePure(declaration.Initializer):
                    case StructuredSet assignment when TrapInlinePure(assignment.Value):
                        body.Add(stmt);
                        break;
                    case StructuredIf conditional when TrapInlinePure(conditional.Condition):
                        body.Add(conditional with {
                            Then = TrapInlineStatements(conditional.Then),
                            Else = conditional.Else is null ? null : TrapInlineStatements(conditional.Else)
                        });
                        break;
                    case StructuredLocalExit { Value: { } value } returned when
                        value.Type == IrValueType.I64 && TrapInlinePure(value):
                        body.Add(new StructuredLocalExit(returned.Offset,
                            new StructuredResult(returned.Offset, value, true, IrValueType.ResultI64)));
                        break;
                    case StructuredSignal { Effect: CfgGuestEffect.Throw, Error: StructuredGuestError } signal:
                        body.Add(new StructuredLocalExit(signal.Offset,
                            new StructuredResult(signal.Offset, signal.Error, false, IrValueType.ResultI64)));
                        break;
                    // Terminal effects remain terminal and never become Err.
                    case StructuredSignal { Effect: CfgGuestEffect.Panic or CfgGuestEffect.Raise,
                        Error: StructuredGuestError }:
                        body.Add(stmt);
                        break;
                    default:
                        throw Error("E_TRAP_INLINE_TASK_UNSUPPORTED: operation requires verified lexical error or cleanup edges");
                }
            }
            return body.AsReadOnly();
        }

        private StructuredExpr NewResult(int at, bool success, int depth)
        {
            Expect("(");
            var payload = !success && IsOrdinaryText(Current.Text)
                ? new StructuredGuestText(Current.Offset, ErrorMessage()) : Expr(depth + 1);
            Expect(")");
            IrValueType result;
            if (success)
            {
                if (payload.Type is not (IrValueType.I64 or IrValueType.OptionI64))
                    throw Error("Ok requires int or Option<int> in this Result ABI");
                result = expectedExpressionType is { } target && target.IsResult() && target.Payload() == payload.Type
                    ? target : GuestResultTypes.Wrap(payload.Type);
            }
            else
            {
                if (expectedExpressionType is not { } expected || !expected.IsResult())
                    throw Error("Err requires a contextual Result success type");
                if (!(payload.Type == IrValueType.GuestError && expected.HasError() ||
                      payload.Type == IrValueType.GuestText && expected.HasTextError()))
                    throw Error("Err payload does not match the declared Result error type");
                result = expected;
            }
            return TypedNode(new StructuredResult(at, payload, success, result), payload);
        }

        private StructuredExpr TaggedPostfix(StructuredExpr value, int depth)
        {
            if (value.Type.IsResult() && Take("."))
            {
                int at = Current.Offset;
                string name = Current.Text;
                index++;
                Expect("(");
                StructuredExpr? fallback = null;
                var (operation, type) = name switch
                {
                    "is_ok" => (CfgOpcode.ResultIsOk, IrValueType.Bool),
                    "is_err" => (CfgOpcode.ResultIsErr, IrValueType.Bool),
                    "unwrap_err" when value.Type == IrValueType.ResultI64Text =>
                        (CfgOpcode.ResultUnwrapTextErr, IrValueType.GuestText),
                    "unwrap_err" when value.Type != IrValueType.ResultI64TextOrError =>
                        (CfgOpcode.ResultUnwrapErr, IrValueType.GuestError),
                    "unwrap" => (CfgOpcode.ResultUnwrap, value.Type.Payload()),
                    "unwrap_or" when value.Type.Payload() == IrValueType.I64 => (CfgOpcode.ResultUnwrapOr, IrValueType.I64),
                    _ => throw Error("Unsupported Result inspection operation")
                };
                if (operation == CfgOpcode.ResultUnwrapOr)
                {
                    fallback = Expr(depth + 1);
                    Require(fallback, type, "Result fallback has wrong type");
                }
                Expect(")");
                var selected = new StructuredResultInspect(at, value, operation, type, fallback);
                value = fallback is null ? TypedNode(selected, value) : TypedNode(selected, value, fallback);
            }
            if (value.Type == IrValueType.GuestError && Take("."))
            {
                Expect("message");
                if (!Take("==") && !Take("eq"))
                    throw Error("The Error.message slice supports equality with a literal string");
                value = TypedNode(new StructuredErrorTest(value.Offset, value, ErrorMessage(), IsType: false), value);
            }
            return OptionPostfix(value, depth);
        }

        private StructuredExpr PropagatePostfix(int depth)
        {
            var operand = Unary(depth + 1);
            if (!operand.Type.IsResult() && operand.Type != IrValueType.OptionI64) return operand;
            if (!Take("?")) return operand;
            if (inlineTaskDepth != 0)
                throw Error("E_SYNC_TASK_PROPAGATION_UNSUPPORTED: postfix ? requires a typed inline failure return");
            if (trapOperandDepth != 0)
                throw Error("E_TRAP_PROPAGATION_UNSUPPORTED: postfix ? inside a trap operand requires a nonlocal exit");
            if (activeReturnType != operand.Type)
                throw Error("Postfix ? requires an enclosing callable with the same Result or Option type");
            IrValueType payload = operand.Type == IrValueType.OptionI64 ? IrValueType.I64 : operand.Type.Payload();
            return TypedNode(new StructuredPropagate(operand.Offset, operand, payload), operand);
        }

        private string ErrorMessage()
        {
            string token = Current.Text;
            if (!IsOrdinaryText(token))
                throw Error("Error requires an ordinary literal message");
            int at = Current.Offset;
            index++;
            var message = new StringBuilder(token.Length - 2);
            for (int i = 1; i < token.Length - 1; i++)
            {
                char ch = token[i];
                if (ch != '\\') { message.Append(ch); continue; }
                ch = token[++i];
                if (ch == 'u')
                {
                    message.Append((char)ushort.Parse(token.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture));
                    i += 4;
                    continue;
                }
                if (ch is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')
                {
                    if (ch == '\r' && token[i + 1] == '\n') i++;
                    continue;
                }
                message.Append(ch switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => ch });
            }
            if (message.Length > 16_384)
                throw new FormatException($"Guest Error message exceeds the literal budget at offset {at}");
            return message.ToString();
        }

        private static bool IsOrdinaryText(string token) => token.Length >= 2 && token[0] is '\'' or '"';
    }
}
