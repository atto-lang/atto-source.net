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
  bool-unsupported:E_PARSE \
  no-main:E_ENTRY; do
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
echo "ATTO_DIRECT_CIL_NEGATIVE_PASS checks=12"
dotnet build "$host" -c Release
expect_42 CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-nativeaot
test -x /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host
expect_42 NATIVEAOT_GUEST /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host

# Additional genuine CIL guest executions; neither calculation is in C#.
for fixture in fixtures/cil/precedence.atto fixtures/cil/forward-call.atto; do
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  dotnet build "$host" -c Release
  expect_42 EXTRA_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
done
echo "ATTO_DIRECT_CIL_POSITIVE_PASS fixtures=3"

# Compiled guest parity for the bounded non-owning local Pointer<int> profile.
# Each source fixture is compiled to CIL, then executed in CoreCLR AND
# in a separately published NativeAOT executable. The C# bridge is fixed;
# the return value originates exclusively in compiled .atto guest code.
for fixture in \
  fixtures/cil/pointer-local-rt.atto \
  fixtures/cil/pointer-local-mixed-bang.atto \
  fixtures/cil/pointer-local-all-bang.atto; do
  name="$(basename "$fixture" .atto)"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release
  expect_42 POINTER_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
  output="/tmp/atto-cil-nativeaot-$name"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
    --self-contained true -o "$output"
  test -x "$output/Atto.Cil.Guest.Host"
  expect_42 POINTER_NATIVEAOT_GUEST "$output/Atto.Cil.Guest.Host"
done
echo "ATTO_POINTER_LOCAL_CIL_NATIVEAOT_PASS guests=3"

# All pointer-negative fixtures require a scoped E_POINTER_* failure and
# an empty emitted-artifact directory. This is compiled-source rejection,
# NOT a regex-only lint or a host-model-only witness.
pointer_reject_count=0
for fixture in fixtures/cil/negative/pointer-*.atto; do
  diagnostic="$(sed -n 's|^// expect-error: ||p' "$fixture")"
  test -n "$diagnostic" || { echo "MISSING_POINTER_DIAGNOSTIC $fixture" >&2; exit 1; }
  rm -f artifacts/Rejected.dll
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "$fixture" artifacts/Rejected.dll \
    >/tmp/atto-pointer-negative.stdout 2>/tmp/atto-pointer-negative.stderr
  result=$?
  set -e
  test "$result" -eq 2 || { echo "POINTER_NEGATIVE_STATUS_FAILED $fixture rc=$result" >&2; exit 1; }
  grep -Fq "$diagnostic:" /tmp/atto-pointer-negative.stderr || {
    cat /tmp/atto-pointer-negative.stderr
    echo "POINTER_NEGATIVE_DIAGNOSTIC_FAILED $fixture expected=$diagnostic" >&2
    exit 1
  }
  test ! -e artifacts/Rejected.dll || {
    echo "POINTER_NEGATIVE_CIL_ARTIFACT_LEAK $fixture" >&2
    exit 1
  }
  pointer_reject_count=$((pointer_reject_count + 1))
done
test "$pointer_reject_count" -eq 19
echo "ATTO_POINTER_LOCAL_CIL_NEGATIVE_PASS rejected=$pointer_reject_count"
