#!/usr/bin/env bash
# Real Atto .atto -> direct ECMA-335 CIL -> managed CLR and NativeAOT.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts

expect_42() {
  local label="$1"; shift
  local result
  set +e
  "$@"
  result=$?
  set -e
  if [[ "$result" -ne 42 ]]; then
    echo "EXPECTED_EXIT_42_FAILED label=$label actual=$result" >&2
    exit 1
  fi
  echo "ATTO_DIRECT_CIL_$label"_PASS exit=42
}
dotnet build "$compiler" -c Release
dotnet run --no-build -c Release --project "$compiler" -- fixtures/cil/exit42.atto "$guest"
test -s "$guest"

for case in \
  duplicate:E_DUP_FUNCTION \
  undefined:E_SYMBOL \
  arity:E_ARITY \
  cycle:E_CYCLE \
  unsupported-actor:E_PARSE \
  main-arity:E_ENTRY \
  overflow:E_LITERAL \
  unknown-parameter:E_SYMBOL \
  no-return:E_PARSE \
  duplicate-parameter:E_DUP_PARAM \
  bool-unsupported:E_RETURN_TYPE \
  no-main:E_ENTRY \
  int-division-float-required:E_DIVISION_FLOAT_RESULT \
  int-division-in-helper:E_DIVISION_FLOAT_RESULT \
  int-division-nested:E_DIVISION_FLOAT_RESULT \
  signed-min-underflow:E_LITERAL \
  float-result-in-int-return:E_DIVISION_FLOAT_RESULT \
  float-result-as-int-arg:E_TYPE \
  unsupported-float-addition:E_DIVISION_FLOAT_RESULT; do
  name="$(echo "$case" | cut -d: -f1)"
  diagnostic="$(echo "$case" | cut -d: -f2)"
  rm -f artifacts/Rejected.dll
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/negative/$name.atto" artifacts/Rejected.dll \
    >/tmp/atto-negative.stdout 2>/tmp/atto-negative.stderr
  result=$?
  set -e
  test "$result" -eq 2 || { echo "NEGATIVE_STATUS_FAILED case=$name exit=$result"; exit 1; }
  grep -Fq "$diagnostic:" /tmp/atto-negative.stderr || {
    cat /tmp/atto-negative.stderr; echo "NEGATIVE_DIAGNOSTIC_FAILED case=$name"; exit 1;
  }
  test ! -e artifacts/Rejected.dll || { echo "NEGATIVE_ARTIFACT_LEAK case=$name"; exit 1; }
done
echo "ATTO_DIRECT_CIL_NEGATIVE_PASS checks=19"
dotnet build "$host" -c Release
expect_42 CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-nativeaot
test -x /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host
expect_42 NATIVEAOT_GUEST /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host

# Additional genuine CIL guest executions; neither calculation is in C#.
for fixture in fixtures/cil/precedence.atto fixtures/cil/forward-call.atto fixtures/cil/signed-min-boundary.atto; do
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  dotnet build "$host" -c Release
  expect_42 EXTRA_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
  if [[ "$fixture" == "fixtures/cil/signed-min-boundary.atto" ]]; then
    # The valid signed minimum must execute through both real compilation paths.
    dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
      --self-contained true -o /tmp/atto-cil-signed-min-nativeaot
    test -x /tmp/atto-cil-signed-min-nativeaot/Atto.Cil.Guest.Host
    expect_42 SIGNED_MIN_NATIVEAOT_GUEST /tmp/atto-cil-signed-min-nativeaot/Atto.Cil.Guest.Host
  fi
done
echo "ATTO_DIRECT_CIL_POSITIVE_PASS fixtures=4"

# Actual typed int/int -> float code generation with verified CIL method
# signatures, checked integer subexpressions and signed operands.
# The bridge checks generated guest output; it does not perform guest division.
float_host=tests/Atto.Cil.Float.Guest.Host/Atto.Cil.Float.Guest.Host.csproj
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/float-division.atto "$guest"
test -s "$guest"
dotnet build "$float_host" -c Release
expect_42 FLOAT_CORECLR_GUEST dotnet run --no-build -c Release --project "$float_host"
dotnet publish "$float_host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-float-nativeaot
test -x /tmp/atto-cil-float-nativeaot/Atto.Cil.Float.Guest.Host
expect_42 FLOAT_NATIVEAOT_GUEST /tmp/atto-cil-float-nativeaot/Atto.Cil.Float.Guest.Host
echo "ATTO_DIRECT_CIL_FLOAT_DIVISION_POSITIVE_PASS fixtures=1"

