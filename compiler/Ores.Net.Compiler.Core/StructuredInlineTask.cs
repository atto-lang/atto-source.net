namespace Ores.Net.Compiler;

internal sealed record StructuredLocalExit(int At, StructuredExpr? Value) : StructuredStmt(At);
internal sealed record StructuredInlineCooperate(int At) : StructuredStmt(At);
internal sealed record StructuredInlineTask(IReadOnlyList<StructuredStmt> Body, IrValueType Type, bool ExitsScope, bool WriteBack);
internal sealed record StructuredDoSyncTask(int At, StructuredInlineTask Task) : StructuredStmt(At);
internal sealed record StructuredSyncTaskValue(int At, StructuredInlineTask Task) : StructuredExpr(At, Task.Type);

public static partial class OresStructuredParser
{
    private sealed partial class Reader
    {
        private sealed class LocalReturnScope(string diagnostic, IrValueType? type, int loopFloor)
        {
            public string Diagnostic { get; } = diagnostic;
            public int LoopFloor { get; } = loopFloor;
            public IrValueType? Type { get; set; } = type;
            public bool HasExit { get; set; }
        }

        private LocalReturnScope? localReturn;
        private int inlineTaskDepth;
        private int? inlineValueBindingFloor;
        private int? directInlineWritebackOffset;

        // Lexical lookahead only selects the supported statement context.
        // The ordinary typed parser still checks the complete task and result.
        private bool IsDirectInlineInitializer()
        {
            if (!AtSyncTask()) return false;
            int cursor = index + 2;
            while (cursor < tokens.Count && tokens[cursor].Text is not ("{" or ";" or "}" or ""))
                cursor++;
            if (cursor >= tokens.Count || tokens[cursor].Text != "{") return false;
            int braces = 0;
            for (; cursor < tokens.Count; cursor++)
            {
                if (tokens[cursor].Text == "{") braces++;
                else if (tokens[cursor].Text == "}" && --braces == 0)
                    return cursor + 1 < tokens.Count && tokens[cursor + 1].Text == ";";
            }
            return false;
        }

        private bool AtSyncTask() => Current.Text == "sync" && tokens[index + 1].Text == "task";

        private StructuredLocalExit ReadLocalExit(int at, int depth, LocalReturnScope scope)
        {
            StructuredExpr? value = null;
            if (Current.Text == ";" || Take("void"))
            {
                if (scope.Type is not (null or IrValueType.Void))
                    throw Error($"{scope.Diagnostic}: a value return is required");
            }
            else
            {
                if (scope.Type == IrValueType.Void)
                    throw Error($"{scope.Diagnostic}: this scope requires a void return");
                var expected = scope.Type ?? expectedExpressionType;
                value = expected is { } type ? ExpectedExpr(depth + 1, type) : Expr(depth + 1);
                if (value.Type is IrValueType.Void or IrValueType.FixedArrayI64)
                    throw Error($"{scope.Diagnostic}: inline task results require a supported scalar or tagged layout");
            }
            Expect(";");
            var returned = value?.Type ?? IrValueType.Void;
            if (scope.Type is { } expectedReturn && expectedReturn != returned)
                throw Error($"{scope.Diagnostic}: return type does not match the inline result");
            scope.Type = returned;
            scope.HasExit = true;
            return new StructuredLocalExit(at, value);
        }

        private StructuredInlineTask ReadInlineTask(int depth, bool imperative, bool writeBack = false)
        {
            Budget(depth);
            IrValueType? declared = imperative ? IrValueType.Void : null;
            if (Take("<"))
            {
                declared = Take("void") ? IrValueType.Void : Type();
                Expect(">");
            }
            if (imperative && declared != IrValueType.Void || !imperative && declared == IrValueType.Void)
                throw Error("E_TASK_RETURN_TYPE: do sync task requires void and value-context sync task requires a value");
            if (declared == IrValueType.FixedArrayI64)
                throw Error("E_TASK_RETURN_TYPE: inline task array results require a verified aggregate layout");

            var scope = new LocalReturnScope("E_TASK_RETURN_TYPE", declared, loopDepth);
            var previous = localReturn;
            var previousFloor = inlineValueBindingFloor;
            // Never widen an inherited write restriction when a nested
            // statement initializer is encountered inside a value expression.
            if (!imperative && !writeBack) inlineValueBindingFloor = scopes.Count;
            localReturn = scope;
            inlineTaskDepth++;
            try
            {
                var (body, terminal) = Block(depth + 1);
                if (!imperative && (!terminal || !scope.HasExit || scope.Type is null or IrValueType.Void))
                    throw Error("E_TASK_RETURN_TYPE: value-context sync task must explicitly return a value on every normal path");
                return new StructuredInlineTask(body, scope.Type!.Value, terminal && !scope.HasExit,
                    imperative || writeBack);
            }
            finally
            {
                inlineTaskDepth--;
                inlineValueBindingFloor = previousFloor;
                localReturn = previous;
            }
        }
    }
}
