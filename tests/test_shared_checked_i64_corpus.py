"""Source-only oracle for the exact Java/JS/11-backend signed-i64 corpus.

This file NEVER compiles or executes Atto guest source. It must not be used as
.NET CIL or NativeAOT guest-execution evidence; see the linked parity contract.
"""
import ast
import hashlib
from pathlib import Path
import re
import unittest

CORPUS = (Path(__file__).resolve().parents[1] /
          "contracts/checked-i64-all-backends-v1/cases.tsv")
GIT_BLOB = "71309fac2ab6b2191a0056c84d78987bd342dce0"
SHA256 = "e56dfccea11757ff18fc72a81ba5d643380e69184ab309eedf45e1e7b940e133"
MIN_I64, MAX_I64 = -(1 << 63), (1 << 63) - 1
OPERATIONS = {"sum": 2, "subtract": 2, "multiply": 2,
              "opposite": 1, "remainder": 2}


class CheckedI64Fault(ValueError):
    def __init__(self, kind):
        super().__init__(kind)
        self.kind = kind


def checked(value):
    if not MIN_I64 <= value <= MAX_I64:
        raise CheckedI64Fault("overflow")
    return value


def evaluate(node):
    """Evaluate a closed, no-IO arithmetic AST; check each intermediate."""
    if isinstance(node, ast.Constant) and type(node.value) is int:
        if 0 <= node.value <= MAX_I64:
            return node.value
        raise ValueError("out-of-range signed i64 literal")
    if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub):
        # The sole legal positive 2**63 magnitude is the lexeme for i64::MIN.
        if (isinstance(node.operand, ast.Constant)
                and type(node.operand.value) is int
                and node.operand.value == 1 << 63):
            return MIN_I64
        return checked(-evaluate(node.operand))
    if (not isinstance(node, ast.Call) or not isinstance(node.func, ast.Name)
            or node.keywords or node.func.id not in OPERATIONS
            or len(node.args) != OPERATIONS[node.func.id]):
        raise ValueError("unrecognized or unsafe source arithmetic")
    # Evaluate left-to-right, including nested expressions. Errors do not
    # disappear if a later operation would return the value to range.
    operands = [evaluate(arg) for arg in node.args]
    op = node.func.id
    if op == "sum":
        return checked(operands[0] + operands[1])
    if op == "subtract":
        return checked(operands[0] - operands[1])
    if op == "multiply":
        return checked(operands[0] * operands[1])
    if op == "opposite":
        return checked(-operands[0])
    left, right = operands
    if right == 0:
        raise CheckedI64Fault("zero")
    # Python's % is Euclidean for positive divisors. The source corpus
    # requires signed truncating remainder, including rem(-7, 3) == -1.
    return checked((abs(left) % abs(right)) * (-1 if left < 0 else 1))


def verify_rows(raw):
    if not raw.endswith(b"\n") or b"\r" in raw:
        raise ValueError("source corpus must have canonical LF line endings")
    rows = raw.decode("utf-8", errors="strict").splitlines()
    if len(rows) != 15:
        raise ValueError("exactly 15 shared signed-i64 cases are mandatory")
    counts = {"value": 0, "overflow": 0, "zero": 0}
    seen = set()
    for row in rows:
        fields = row.split("|")
        if len(fields) != 4:
            raise ValueError("unexpected checked-i64 corpus column count")
        case_id, kind, source, expected = fields
        if (not re.fullmatch(r"[a-z]+(?:-[a-z]+)*", case_id)
                or case_id in seen or kind not in counts
                or not re.fullmatch(r"[A-Za-z0-9(),+\- ]{1,160}", source)):
            raise ValueError("unknown or duplicated signed-i64 case")
        seen.add(case_id)
        counts[kind] += 1
        try:
            observed = evaluate(ast.parse(source, mode="eval").body)
            fault = None
        except CheckedI64Fault as error:
            observed, fault = None, error.kind
        if kind == "value":
            if (fault is not None or not re.fullmatch(r"-?(?:0|[1-9][0-9]*)", expected)
                    or observed != int(expected)):
                raise ValueError("wrong checked-i64 value: " + case_id)
        elif kind == "overflow":
            if fault != "overflow" or expected != "ORES_I64_OVERFLOW":
                raise ValueError("wrong signed-overflow classification: " + case_id)
        elif fault != "zero" or expected != "DIVISION_BY_ZERO":
            raise ValueError("wrong zero-divisor classification: " + case_id)
    if counts != {"value": 7, "overflow": 7, "zero": 1}:
        raise ValueError("changed signed-i64 case profile")
    return counts


class SharedCheckedI64SourceOnlyTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.raw = CORPUS.read_bytes()

    def test_byte_identical_java_js_conformance_corpus(self):
        raw = self.raw
        blob = hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") +
                            b"\0" + raw).hexdigest()
        self.assertEqual(blob, GIT_BLOB)
        self.assertEqual(hashlib.sha256(raw).hexdigest(), SHA256)
        self.assertEqual(verify_rows(raw), {"value": 7, "overflow": 7, "zero": 1})

    def test_intermediate_overflow_is_not_canceled(self):
        for source in ("subtract(sum(9223372036854775807,1),1)",
                       "sum(opposite(-9223372036854775808),1)"):
            with self.subTest(source=source):
                with self.assertRaisesRegex(CheckedI64Fault, "overflow"):
                    evaluate(ast.parse(source, mode="eval").body)

    def test_signed_remainder_min_and_negative(self):
        for expression, expected in (("remainder(-7,3)", -1),
                                     ("remainder(-9223372036854775808,-1)", 0)):
            self.assertEqual(evaluate(ast.parse(expression, mode="eval").body), expected)

    def test_zero_divisor_never_admitted(self):
        with self.assertRaisesRegex(CheckedI64Fault, "zero"):
            evaluate(ast.parse("remainder(1,0)", mode="eval").body)

    def test_unsafe_ast_never_executes_or_readjusts_arithmetic(self):
        for expression in ("__import__('os')", "sum(1,2,3)", "foo(1)",
                           "sum(1,2.0)", "sum(1,True)", "[1,2]", "1+2",
                           "remainder(1,0,9)", "-9223372036854775809"):
            with self.subTest(expression=expression):
                with self.assertRaises(ValueError):
                    evaluate(ast.parse(expression, mode="eval").body)

    def test_corpus_mutations_and_duplicates_are_not_success(self):
        lines = self.raw.decode("utf8").splitlines()
        mutations = (
            ("\n".join(lines[:-1]) + "\n").encode(),
            ("\n".join(lines[:-1] + [lines[0]]) + "\n").encode(),
            self.raw.replace(b"safe-zero|value|sum(0,0)|0",
                             b"safe-zero|value|sum(0,0)|1"),
            self.raw.replace(b"zero-divisor|zero|remainder(1,0)|DIVISION_BY_ZERO",
                             b"zero-divisor|value|remainder(1,0)|0"),
            self.raw.replace(b"overflow-cancel|overflow|", b"overflow-cancel|value|"),
            self.raw.replace(b"\n", b"\r\n"),
        )
        for mutated in mutations:
            with self.subTest(sha256=hashlib.sha256(mutated).hexdigest()[:12]):
                with self.assertRaises(ValueError):
                    verify_rows(mutated)


if __name__ == "__main__":
    unittest.main()
