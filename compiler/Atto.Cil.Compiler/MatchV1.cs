using System.Reflection.Metadata.Ecma335;
using System.Reflection.Metadata;

namespace Atto.Cil.Compiler;

// Restricted integer match expression; records keep the binder's lifetime
// attached to a single arm and never expose mutable ambient state.
internal sealed record ValueMatchArm(long? Literal, string? Binding, bool Wildcard,
    Expression? Guard, Expression Body);
internal sealed record ValueMatch(Expression Subject, List<ValueMatchArm> Arms) : Expression;

internal sealed partial class Parser
{
    private Expression ParseMatch(int depth)
    {
        if (depth > 32) throw new CompilerError("E_LIMIT", "match nesting limit");
        var subject = ParseExpr(0, depth + 1);
        Require("over");
        List<ValueMatchArm> arms = [];
        while (Peek != "end")
        {
            if (Peek == "<eof>") throw new CompilerError("E_PARSE", "unterminated match");
            if (arms.Count >= 64) throw new CompilerError("E_LIMIT", "64 match arm limit");
            Require("on");
            long? literal = null;
            string? binding = null;
            bool wildcard = Eat("_");
            if (!wildcard)
            {
                bool negative = Eat("-");
                if (Peek.Length > 0 && Peek[0] is >= '0' and <= '9')
                {
                    string raw = (negative ? "-" : "") + Peek;
                    if (!long.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture, out long n))
                        throw new CompilerError("E_LITERAL", "match pattern integer out of range");
                    literal = n;
                    _position++;
                }
                else
                {
                    if (negative) throw new CompilerError("E_PARSE", "expected numeric pattern after minus");
                    binding = Name();
                    if (binding is "on" or "when" or "end" or "over" or "match")
                        throw new CompilerError("E_PARSE", "reserved word in match pattern");
                }
            }
            var guard = Eat("when") ? ParseExpr(0, depth + 1) : null;
            Require("->");
            bool braced = Eat("{");
            Expression body;
            if (braced)
            {
                // This bounded profile only admits a block whose first and
                // only statement returns a value, with no hidden fallthrough.
                if (!Eat("return") && !Eat("^"))
                    throw new CompilerError("E_RETURN", "match value block may not fall through");
                body = ParseExpr(0, depth + 1);
                Require(";");
                Require("}");
            }
            else body = ParseExpr(0, depth + 1);
            arms.Add(new ValueMatchArm(literal, binding, wildcard, guard, body));
            if (!Eat(",") && !braced && Peek != "end")
                throw new CompilerError("E_PARSE", "comma required after a value match arm");
        }
        Require("end");
        if (arms.Count == 0) throw new CompilerError("E_MATCH", "match requires arms");
        return new ValueMatch(subject, arms);
    }
}

internal sealed partial class CIL
{
    private int _nextMatchLocal;

    // Every match subject gets its own i64 local. A subject expression can
    // invoke a function and must be evaluated precisely once.
    private static int CountMatchLocals(Expression e) => e switch
    {
        ValueMatch m => 1 + CountMatchLocals(m.Subject) +
            m.Arms.Sum(a => CountMatchLocals(a.Body) +
                (a.Guard is null ? 0 : CountMatchLocals(a.Guard))),
        Binary b => CountMatchLocals(b.Left) + CountMatchLocals(b.Right),
        Call c => c.Args.Sum(CountMatchLocals),
        _ => 0
    };

    private static bool CheckValueMatch(ValueMatch m, HashSet<string> bound,
        Func<Expression, HashSet<string>, bool> check)
    {
        if (check(m.Subject, bound))
            throw new CompilerError("E_TYPE", "match subject must be int");
        bool covered = false;
        HashSet<long> seen = [];
        foreach (var arm in m.Arms)
        {
            if (covered || (arm.Literal is long value && seen.Contains(value)))
                throw new CompilerError("E_MATCH", "unreachable match arm");
            var scope = new HashSet<string>(bound, StringComparer.Ordinal);
            if (arm.Binding is not null) scope.Add(arm.Binding);
            if (arm.Guard is not null && !check(arm.Guard, scope))
                throw new CompilerError("E_TYPE", "match guard must have bool type");
            if (check(arm.Body, scope))
                throw new CompilerError("E_TYPE", "match value arm must return int");
            if (arm.Guard is null)
            {
                if (arm.Wildcard || arm.Binding is not null) covered = true;
                else if (arm.Literal is long n) seen.Add(n);
            }
        }
        if (!covered)
            throw new CompilerError("E_MATCH",
                "non-exhaustive integer match requires an unguarded on _ or binding fallback");
        return false;
    }

    private void EmitValueMatch(ValueMatch m, Function f, InstructionEncoder il,
        Dictionary<string, int>? outer)
    {
        int slot = _nextMatchLocal++;
        Generate(m.Subject, f, il, outer);
        il.StoreLocal(slot);
        var complete = il.DefineLabel();
        foreach (var arm in m.Arms)
        {
            var next = il.DefineLabel();
            if (arm.Literal is long literal)
            {
                il.LoadLocal(slot);
                il.LoadConstantI8(literal);
                il.OpCode(ILOpCode.Ceq);
                il.Branch(ILOpCode.Brfalse, next);
            }
            var scope = outer is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(outer, StringComparer.Ordinal);
            if (arm.Binding is not null) scope[arm.Binding] = slot;
            if (arm.Guard is not null)
            {
                Generate(arm.Guard, f, il, scope);
                il.Branch(ILOpCode.Brfalse, next);
            }
            Generate(arm.Body, f, il, scope);
            il.Branch(ILOpCode.Br, complete);
            il.MarkLabel(next);
        }
        // Statically unreachable if CheckValueMatch succeeded. If any
        // invariant is violated, fail closed rather than inventing a value.
        il.OpCode(ILOpCode.Ldnull);
        il.OpCode(ILOpCode.Throw);
        il.MarkLabel(complete);
    }
}
