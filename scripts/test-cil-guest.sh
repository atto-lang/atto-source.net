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
  no-main:E_ENTRY \
  taskfuture-typed-constructor-unsupported:E_TASK_UNSUPPORTED \
  taskfuture-get-ready-unsupported:E_LEX \
  taskfuture-cancel-task-unsupported:E_LEX \
  taskfuture-cooperate-unsupported:E_LEX \
  taskfuture-receipt-split-unsupported:E_LEX \
  typed-task-unsupported:E_TASK_UNSUPPORTED \
  do-task-unsupported:E_TASK_UNSUPPORTED \
  do-sync-task-nonvoid:E_TASK_RETURN_TYPE \
  do-sync-task-inferred-nonvoid:E_TASK_RETURN_TYPE \
  do-sync-task-nested-nonvoid:E_TASK_RETURN_TYPE \
  sync-task-standalone-void:E_SYNTAX \
  sync-task-standalone-typed-void:E_SYNTAX \
  sync-task-nested-standalone-void:E_SYNTAX \
  sync-task-standalone-nonvoid:E_SYNTAX \
  sync-task-missing-return:E_TASK_RETURN_TYPE \
  sync-task-void-as-value:E_TASK_RETURN_TYPE \
  sync-task-bad-type:E_TASK_UNSUPPORTED \
  sync-task-await-unsupported:E_TASK_UNSUPPORTED \
  sync-task-void-return-type:E_TASK_RETURN_TYPE \
  sync-task-double-return:E_PARSE \
  sync-reserved:E_RESERVED; do
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
echo "ATTO_DIRECT_CIL_NEGATIVE_PASS checks=33"
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
# These are genuine generated Atto CIL, not C# task emulation.
# Test pure, empty, nested, discarded and value-producing sync task forms.
for fixture in \
    fixtures/cil/sync-task-empty.atto \
    fixtures/cil/sync-task-inline-value.atto \
    fixtures/cil/sync-task-nested-discard.atto \
    fixtures/cil/sync-task-function-call.atto; do
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  dotnet build "$host" -c Release
  expect_42 INLINE_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
done

# Verify one nested inline fixture through the NativeAOT bridge as well.
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/sync-task-nested-discard.atto "$guest"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-inline-nativeaot
test -x /tmp/atto-cil-inline-nativeaot/Atto.Cil.Guest.Host
expect_42 INLINE_NATIVEAOT_GUEST /tmp/atto-cil-inline-nativeaot/Atto.Cil.Guest.Host
echo "ATTO_DIRECT_CIL_POSITIVE_PASS fixtures=7 inline=4"
