using Ores.Net.Compiler;

internal static class CfgVerifierTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("CFG test failed: " + message);
    }

    private static void Reject(CfgFunction function, string why)
    {
        try { _ = OresCfgVerifier.Verify(function); }
        catch (FormatException) { return; }
        throw new Exception("Expected CFG verification failure: " + why);
    }

    private static CfgBlock Block(
        string name, CfgParameter[] parameters,
        CfgInstruction[] instructions, CfgTerminator terminator) =>
        new(name, parameters, instructions, terminator);

    public static void Run()
    {
        // Loop-carried i64 value. Typed block parameters serve as SSA phi
        // arguments on the entry edge and the backedge.
        var entry = Block("entry", Array.Empty<CfgParameter>(),
            new[] {
                new CfgInstruction(0, CfgOpcode.ConstantI64, IrValueType.I64,
                    I64Constant: 0)
            }, new CfgJump("header", new[] { 0 }));

        var header = Block("header",
            new[] { new CfgParameter(1, IrValueType.I64) },
            new[] {
                new CfgInstruction(2, CfgOpcode.ConstantI64, IrValueType.I64,
                    I64Constant: 5),
                new CfgInstruction(3, CfgOpcode.EqualI64, IrValueType.Bool,
                    Left: 1, Right: 2)
            }, new CfgBranch(3, "exit", new[] { 1 }, "body", new[] { 1 }));

        var body = Block("body",
            new[] { new CfgParameter(4, IrValueType.I64) },
            new[] {
                new CfgInstruction(5, CfgOpcode.ConstantI64, IrValueType.I64,
                    I64Constant: 1),
                new CfgInstruction(6, CfgOpcode.CheckedAdd, IrValueType.I64,
                    Left: 4, Right: 5)
            }, new CfgJump("header", new[] { 6 }));

        var finish = Block("exit",
            new[] { new CfgParameter(7, IrValueType.I64) },
            Array.Empty<CfgInstruction>(), new CfgReturn(7));

        var loop = new CfgFunction("count", IrValueType.I64, "entry",
            new[] { entry, header, body, finish });
        var verifiedLoop = OresCfgVerifier.Verify(loop);
        Expect(CfgReferenceEvaluator.Evaluate(verifiedLoop) == 5,
            "loop-carried block parameter correctly increments to 5");

        try
        {
            _ = CfgReferenceEvaluator.Evaluate(verifiedLoop, maxSteps: 2);
            throw new Exception("CFG evaluator failed to enforce step budget");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("budget",
            StringComparison.Ordinal)) { }

        // Diamond control flow: the join must read its own block parameter,
        // not a value from a predecessor which fails SSA dominance.
        var branchCondition = Block("entry", Array.Empty<CfgParameter>(),
            new[] {
                new CfgInstruction(0, CfgOpcode.ConstantBool, IrValueType.Bool,
                    BoolConstant: true),
                new CfgInstruction(1, CfgOpcode.ConstantI64, IrValueType.I64,
                    I64Constant: 10),
                new CfgInstruction(2, CfgOpcode.ConstantI64, IrValueType.I64,
                    I64Constant: 20)
            }, new CfgBranch(0, "left", new[] { 1 }, "right", new[] { 2 }));
        var left = Block("left",
            new[] { new CfgParameter(3, IrValueType.I64) },
            Array.Empty<CfgInstruction>(), new CfgJump("join", new[] { 3 }));
        var right = Block("right",
            new[] { new CfgParameter(4, IrValueType.I64) },
            Array.Empty<CfgInstruction>(), new CfgJump("join", new[] { 4 }));
        var join = Block("join",
            new[] { new CfgParameter(5, IrValueType.I64) },
            Array.Empty<CfgInstruction>(), new CfgReturn(5));
        var diamond = new CfgFunction("diamond", IrValueType.I64, "entry",
            new[] { branchCondition, left, right, join });
        Expect(CfgReferenceEvaluator.Evaluate(OresCfgVerifier.Verify(diamond)) == 10,
            "diamond passes typed values through join parameters");

        Reject(diamond with { Blocks = new[] {
            branchCondition, left, right, join with { Terminator = new CfgReturn(3) }
        } }, "predecessor-only SSA value does not dominate join");

        Reject(loop with { Blocks = new[] {
            entry, header, body, finish with { Terminator = new CfgReturn(6) }
        } }, "body-only definition does not dominate exit");

        Reject(loop with { Blocks = new[] {
            entry, header with { Terminator = new CfgBranch(1,
                "exit", new[] { 1 }, "body", new[] { 1 }) }, body, finish
        } }, "branch condition must be bool");

        Reject(loop with { Blocks = new[] {
            entry with { Terminator = new CfgJump("header", Array.Empty<int>()) },
            header, body, finish
        } }, "branch argument arity mismatch");

        Reject(loop with { Blocks = new[] {
            entry with { Terminator = new CfgJump("unknown", new[] { 0 }) },
            header, body, finish
        } }, "unknown target rejected");

        Reject(diamond with { Blocks = new[] {
            branchCondition with { Terminator = new CfgBranch(0,
                "left", new[] { 0 }, "right", new[] { 2 }) },
            left, right, join
        } }, "edge argument must match destination block parameter type");

        Reject(diamond with { Blocks = new[] {
            branchCondition,
            left with { Terminator = new CfgJump("join", new[] { 4 }) },
            right, join
        } }, "edge argument cannot use a value defined in a sibling branch");

        Reject(loop with { Blocks = new[] {
            entry,
            header with { Instructions = new[] {
                new CfgInstruction(2, CfgOpcode.ConstantI64, IrValueType.I64,
                    I64Constant: 5),
                new CfgInstruction(3, CfgOpcode.EqualI64, IrValueType.Bool,
                    Left: 1, Right: 1, I64Constant: 7)
            } },
            body, finish
        } }, "binary arithmetic rejects extraneous literal payloads");

        Reject(loop with { Blocks = new[] {
            entry, header, body, finish with { Name = "header" }
        } }, "duplicate block name rejected");

        Reject(loop with { Blocks = new[] {
            entry with { Parameters = new[] { new CfgParameter(8, IrValueType.I64) } },
            header, body, finish
        } }, "entry requires no arguments");

        Reject(loop with { Blocks = new[] {
            entry, header, body, finish,
            Block("orphan", Array.Empty<CfgParameter>(),
                new[] { new CfgInstruction(8, CfgOpcode.ConstantI64,
                    IrValueType.I64, I64Constant: 99) }, new CfgReturn(8))
        } }, "unreachable block rejected");

        Reject(diamond with { Blocks = new[] {
            branchCondition, left, right,
            join with { Terminator = new CfgReturn(0) }
        } }, "return type mismatch bool vs i64");

        Reject(new CfgFunction("bad", IrValueType.I64, "entry",
            new[] { Block("entry", Array.Empty<CfgParameter>(),
                new[] {
                    new CfgInstruction(0, CfgOpcode.CheckedAdd,
                        IrValueType.I64, Left: 1, Right: 1),
                    new CfgInstruction(1, CfgOpcode.ConstantI64,
                        IrValueType.I64, I64Constant: 3)
                }, new CfgReturn(0))
            }), "same-block forward SSA reference rejected");

        Reject(new CfgFunction("bad", IrValueType.I64, "entry",
            new[] { Block("entry", Array.Empty<CfgParameter>(),
                new[] {
                    new CfgInstruction(0, (CfgOpcode)999, IrValueType.I64)
                }, new CfgReturn(0))
            }), "unknown opcodes fail closed");

        // Verified collections are deep frozen: modifying source parameters,
        // instructions or branch-argument lists does not alter the proof.
        var mutableArgs = new List<int> { 0 };
        var mutableOps = new List<CfgInstruction> {
            new(0, CfgOpcode.ConstantI64, IrValueType.I64, I64Constant: 42)
        };
        var mutableParams = new List<CfgParameter> {
            new(1, IrValueType.I64)
        };
        var mutable = new CfgFunction("immutable", IrValueType.I64, "start",
            new[] {
                new CfgBlock("start", Array.Empty<CfgParameter>(), mutableOps,
                    new CfgJump("end", mutableArgs)),
                new CfgBlock("end", mutableParams,
                    Array.Empty<CfgInstruction>(), new CfgReturn(1))
            });
        var approved = OresCfgVerifier.Verify(mutable);
        mutableArgs[0] = 999;
        mutableOps[0] = new CfgInstruction(0, CfgOpcode.ConstantI64,
            IrValueType.I64, I64Constant: -1);
        mutableParams[0] = new CfgParameter(1, IrValueType.Bool);
        Expect(CfgReferenceEvaluator.Evaluate(approved) == 42,
            "verified CFG is immune to mutable source aliases");
    }
}
