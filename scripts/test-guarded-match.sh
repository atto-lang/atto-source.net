#!/usr/bin/env bash
# Direct source-compiled CIL guest execution (NOT a C# imitation of Atto logic).
set -euo pipefail
cd "$(dirname "$0")/.."
compiler="compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj"
host="tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj"
fixture_dir="fixtures/cil/match-return-forms-v1"
guest="artifacts/AttoGuest.dll"
mkdir -p artifacts
check_blob() {
  test "$(git hash-object "$1")" = "$2" || {
    echo "SOURCE_SHA_MISMATCH: $1" >&2; exit 1;
  }
}
check_blob "$fixture_dir/pass/match-guard-block-return.atto" 603df679e1e9b7415cb8b798d4a2143545dd27b8
check_blob "$fixture_dir/pass/match-guard-block-caret.atto" 0b2addfef29cd76d60d90bf081f4d326ea2d70df
check_blob "$fixture_dir/fail/match-guard-no-fallback.atto" 23f8597125af43b3cab294ba8dc9b110d9983ef5
check_blob "$fixture_dir/fail/match-guard-block-fallthrough.atto" a8ec2c699c4a521508bee85f55a49888064c6948
check_blob "$fixture_dir/negative/unsupported-adt-is-pattern.atto" a9aeb19ceedbff713fd60ac348f0a9849f18725a

expect_guest_exit() {
  local expected="$1"; shift
  local result
  set +e
  "$@"
  result=$?
  set -e
  test "$result" = "$expected" || {
    echo "GUEST_EXIT_MISMATCH expected=$expected actual=$result" >&2; exit 1;
  }
}
# Existing main test script builds compiler once. No runtime interpretation.
for item in scalar-return:4 scalar-caret:4 scalar-single-evaluation:42; do
  name="${item%%:*}"; expected="${item#*:}"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture_dir/${name}.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release --no-incremental
  expect_guest_exit "$expected" dotnet run --no-build -c Release --project "$host"
  echo "PASS CoreCLR actual generated CIL $name exit=$expected"
done
# The last generated guest returns 42; test the same CIL through NativeAOT.
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-guarded-match-nativeaot
test -x /tmp/atto-guarded-match-nativeaot/Atto.Cil.Guest.Host
expect_guest_exit 42 /tmp/atto-guarded-match-nativeaot/Atto.Cil.Guest.Host
echo "PASS NativeAOT actual generated CIL scalar-single-evaluation exit=42"

for item in \
  fail/match-guard-no-fallback:E_MATCH \
  fail/match-guard-block-fallthrough:E_RETURN \
  negative/invalid-guard-type:E_TYPE \
  negative/guard-scope-leak:E_SYMBOL \
  negative/unreachable-fallback:E_MATCH \
  negative/boolean-result:E_TYPE \
  negative/unsupported-adt-is-pattern:E_UNSUPPORTED_TYPE; do
  name="${item%%:*}"; code="${item#*:}"
  rm -f artifacts/Rejected.dll
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "$fixture_dir/${name}.atto" artifacts/Rejected.dll > /tmp/atto-match.out 2> /tmp/atto-match.err
  status=$?
  set -e
  test "$status" = 2 || { echo "NEGATIVE_EXIT_ERROR $name status=$status" >&2; exit 1; }
  grep -Fq "$code:" /tmp/atto-match.err || {
    cat /tmp/atto-match.err; echo "NEGATIVE_DIAGNOSTIC_ERROR $name" >&2; exit 1;
  }
  test ! -e artifacts/Rejected.dll || { echo "NEGATIVE_ARTIFACT_ERROR $name" >&2; exit 1; }
  echo "PASS rejected $name $code no-artifact"
done
# Full stdout parity remains unimplemented in the tiny direct-CIL compiler.
# Preserve the exact sources, and fail closed instead of claiming a guest PASS.
for form in return caret; do
  test "$form" = return && fixture=match-guard-block-return || fixture=match-guard-block-caret
  rm -f artifacts/Rejected.dll
  if dotnet run --no-build -c Release --project "$compiler" -- \
      "$fixture_dir/pass/${fixture}.atto" artifacts/Rejected.dll > /tmp/atto-match.out 2> /tmp/atto-match.err; then
    echo "UNEXPECTED_SUPPORTED_STDOUT_GUEST $form (update conformance, run stdout)" >&2; exit 1
  fi
  grep -Eq 'E_LEX:|E_UNSUPPORTED:' /tmp/atto-match.err
  test ! -e artifacts/Rejected.dll
  echo "UNSUPPORTED full stdout user fixture form=$form"
done
echo "ATTO_CIL_GUARDED_MATCH_SCALAR_REGRESSION_COMPLETE original_stdout=UNSUPPORTED"
