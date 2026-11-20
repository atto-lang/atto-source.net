#!/usr/bin/env bash
# Source-only CIL admission for Error/Result/Option; no executable guest
# support is claimed by rejected fixtures.
set -euo pipefail
cd "$(dirname "$0")/.."
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for name in parse-id result-match option-question trap-error throw-error; do
  artifact="$work/$name.dll"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/error-result/$name.atto" "$artifact" \
    >"$work/$name.stdout" 2>"$work/$name.stderr"
  status=$?
  set -e
  test "$status" -eq 2 || { echo "RESULT_ADMISSION_BAD_EXIT $name $status" >&2; exit 1; }
  grep -Fq 'E_UNSUPPORTED_RESULT_EFFECTS:' "$work/$name.stderr" || {
    cat "$work/$name.stderr" >&2
    echo "RESULT_ADMISSION_NO_DIAGNOSTIC $name" >&2
    exit 1
  }
  test ! -e "$artifact" || { echo "RESULT_ADMISSION_ARTIFACT_LEAK $name" >&2; exit 1; }
done

# Comments are not source effects: a valid scalar guest remains compilable.
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/error-result/comment-decoy.atto "$work/comment.dll"
test -s "$work/comment.dll"

# Quoted pseudo-syntax remains a string, not an error-effect token.
set +e
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/error-result/string-decoy.atto "$work/string.dll" \
  >"$work/string.stdout" 2>"$work/string.stderr"
status=$?
set -e
test "$status" -eq 2
grep -Fq 'E_NAME:' "$work/string.stderr"
if grep -Fq 'E_UNSUPPORTED_RESULT_EFFECTS:' "$work/string.stderr"; then
  echo "RESULT_ADMISSION_STRING_INJECTION" >&2
  exit 1
fi
test ! -e "$work/string.dll"
echo "ATTO_DIRECT_CIL_RESULT_EFFECT_ADMISSION_PASS negatives=5 decoys=2 guest_parity=NOT_EXECUTED"
