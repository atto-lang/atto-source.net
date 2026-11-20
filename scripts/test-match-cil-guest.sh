#!/usr/bin/env bash
# Exact-source Atto -> direct CIL -> CoreCLR and NativeAOT guarded return-match gate.
# No C# implementation of the fixture logic: only the fixed entry bridge is C#.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts

for entry in \
  pass/match-guard-block-return:603df679e1e9b7415cb8b798d4a2143545dd27b8 \
  pass/match-guard-block-caret:0b2addfef29cd76d60d90bf081f4d326ea2d70df \
  fail/match-guard-no-fallback:23f8597125af43b3cab294ba8dc9b110d9983ef5 \
  fail/match-guard-block-fallthrough:a8ec2c699c4a521508bee85f55a49888064c6948; do
  path="${entry%%:*}"
  expected="${entry#*:}"
  test "$(git hash-object "fixtures/match-return-forms-v1/${path}.atto")" = "$expected" || {
    echo "EXACT_SHARED_BLOB_MISMATCH path=$path" >&2; exit 1;
  }
done
echo "EXACT_SHARED_MATCH_CORPUS_SHA_VERIFIED=4"
dotnet build "$compiler" -c Release

expect_exit() {
  local label="$1" expected="$2"; shift 2
  local status
  set +e
  "$@"
  status=$?
  set -e
  test "$status" = "$expected" || {
    echo "GUEST_EXECUTION_FAILURE label=$label expected=$expected actual=$status" >&2
    exit 1
  }
  echo "PASS $label compiled guest exit=$status"
}

for form in return caret; do
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/match-return-forms-v1/native-${form}-scalar.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release
  expect_exit "CoreCLR-${form}-match" 4 dotnet run --no-build -c Release --project "$host"
  aot="/tmp/atto-match-${form}-nativeaot"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
    --self-contained true -o "$aot"
  test -x "$aot/Atto.Cil.Guest.Host"
  expect_exit "NativeAOT-${form}-match" 4 "$aot/Atto.Cil.Guest.Host"
done

check_rejected() {
  local name="$1" expected="$2" prefix="$3"
  local artifact=artifacts/MatchRejected.dll
  rm -f "$artifact"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/match-return-forms-v1/${prefix}${name}.atto" "$artifact" \
    >/tmp/atto-match-negative.out 2>/tmp/atto-match-negative.err
  local status=$?
  set -e
  test "$status" = 2 || { echo "NEGATIVE_STATUS_FAILED $name ($status)" >&2; exit 1; }
  grep -Fq "$expected:" /tmp/atto-match-negative.err || {
    cat /tmp/atto-match-negative.err
    echo "NEGATIVE_DIAGNOSTIC_FAILED $name expected=$expected" >&2; exit 1;
  }
  test ! -e "$artifact" || { echo "NEGATIVE_ARTIFACT_LEAK $name" >&2; exit 1; }
  echo "PASS rejected $name $expected no-artifact"
}
check_rejected match-guard-no-fallback E_MATCH fail/
check_rejected match-guard-block-fallthrough E_RETURN fail/
for entry in \
    guard-not-bool:E_TYPE \
    duplicate-unreachable:E_MATCH \
    guarded-no-fallback:E_MATCH \
    binder-leak:E_SYMBOL \
    result-not-int:E_TYPE \
    unguarded-after-default:E_MATCH \
    assignment-expression:E_PARSE; do
  check_rejected "${entry%%:*}" "${entry#*:}" negative/
done

# Exact original positive fixtures use stdio.println, which is deliberately
# not lowered by the restricted direct-CIL backend. This is an explicit
# UNSUPPORTED result, not the positive parity condition.
check_rejected match-guard-block-return E_LEX pass/
check_rejected match-guard-block-caret E_LEX pass/
echo "MATCH_CIL_SCALAR_NATIVEAOT_PASS; FULL_STDOUT_ORIGINALS_UNSUPPORTED"
