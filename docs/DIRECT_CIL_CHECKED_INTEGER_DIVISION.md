# Checked signed division in direct CIL

The older floating-result section records the previous compiler contract.
The [reconciled language target](https://github.com/atto-lang/atto-spec/pull/151)
requires signed `int / int` to truncate toward zero with an integer result.
The direct CIL lane admits signed division with a known nonzero literal
divisor. Dividing by `-1` additionally requires a known dividend other than
the signed minimum. Static zero and overflow have distinct diagnostics.
Unproven divisors and potential signed-minimum overflow fail before writing
a DLL until the backend can emit the typed guest Error paths.
