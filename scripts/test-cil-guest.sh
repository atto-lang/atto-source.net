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

# Functional pipeline conformance is compiled from .atto source, not evaluated
# in shell/C#/Python. The same produced CIL is executed under CoreCLR and,
# for both canonical 83-result cases, a freshly published NativeAOT host.
expect_pipeline_exit() {
  local label="$1" expected="$2"; shift 2
  local actual
  set +e
  "$@"
  actual=$?
  set -e
  test "$actual" -eq "$expected" || {
    echo "ATTO_PIPELINE_EXECUTION_FAILED label=$label expected=$expected actual=$actual" >&2
    exit 1
  }
  echo "ATTO_PIPELINE_EXECUTED label=$label guest_exit=$actual"
}
for spec in \
  block-body-83:83 \
  inline-arrow-83:83 \
  reverse-42:42 \
  grouped-42:42; do
  name="$(printf '%s' "$spec" | cut -d: -f1)"
  expected="$(printf '%s' "$spec" | cut -d: -f2)"
  fixture="fixtures/cil/pipelines/$name.atto"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  test -s "$guest"
  before="$(sha256sum "$guest" | cut -d' ' -f1)"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  after="$(sha256sum "$guest" | cut -d' ' -f1)"
  test "$before" = "$after" || {
    echo "ATTO_PIPELINE_NONDETERMINISTIC_CIL fixture=$name" >&2
    exit 1
  }
  dotnet build "$host" -c Release -t:Rebuild
  expect_pipeline_exit "$name-CORECLR" "$expected" \
    dotnet run --no-build -c Release --project "$host"
  if [[ "$expected" -eq 83 ]]; then
    dotnet publish "$host" -c Release -r linux-x64 \
      -p:PublishAot=true --self-contained true \
      -o "/tmp/atto-cil-pipeline-$name"
    expect_pipeline_exit "$name-NATIVEAOT" "$expected" \
      "/tmp/atto-cil-pipeline-$name/Atto.Cil.Guest.Host"
  fi
done
echo "ATTO_PIPELINE_POSITIVE_GUEST_PASS cases=4 nativeaot_cases=2"

for spec in \
  mixed-forward-reverse:E_PARSE \
  mixed-reverse-forward:E_PARSE \
  missing-semicolon:E_PARSE \
  wrong-type:E_TYPE \
  unknown-call:E_SYMBOL \
  arity:E_ARITY \
  bad-callee:E_PARSE \
  forward-local:E_SYMBOL \
  duplicate-local:E_DUP_LOCAL \
  arrow-brace-no-return:E_PARSE; do
  name="$(printf '%s' "$spec" | cut -d: -f1)"
  diagnostic="$(printf '%s' "$spec" | cut -d: -f2)"
  reject="artifacts/RejectedPipeline.dll"
  rm -f "$reject"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/pipelines/negative/$name.atto" "$reject" \
    > /tmp/atto-pipeline-negative.stdout \
    2> /tmp/atto-pipeline-negative.stderr
  actual=$?
  set -e
  test "$actual" -eq 2 || {
    echo "PIPELINE_NEGATIVE_WRONG_STATUS case=$name actual=$actual" >&2
    exit 1
  }
  grep -Fq "$diagnostic:" /tmp/atto-pipeline-negative.stderr || {
    cat /tmp/atto-pipeline-negative.stderr
    echo "PIPELINE_NEGATIVE_WRONG_DIAGNOSTIC case=$name" >&2
    exit 1
  }
  test ! -e "$reject" || {
    echo "PIPELINE_NEGATIVE_ARTIFACT_LEAK case=$name" >&2
    exit 1
  }
done
echo "ATTO_PIPELINE_NEGATIVE_PASS cases=10"
