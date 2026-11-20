#!/usr/bin/env bash
# Fail-closed admission for proposed Map/Entry; this does NOT prove guest support.
set -euo pipefail
cd "$(dirname "$0")/.."
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for name in dual-entry-add empty-map; do
  artifact="$work/$name.dll"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/map-entry/$name.atto" "$artifact" >"$work/$name.stdout" 2>"$work/$name.stderr"
  status=$?
  set -e
  if [[ "$status" -ne 2 ]]; then
    echo "FAIL: $name must fail closed with exit 2; got $status" >&2
    cat "$work/$name.stderr" >&2
    exit 1
  fi
  grep -Fq 'E_UNSUPPORTED_MAP_ENTRY:' "$work/$name.stderr" || {
    cat "$work/$name.stderr" >&2
    echo "FAIL: missing explicit Map/Entry diagnostic for $name" >&2
    exit 1
  }
  test ! -e "$artifact" || { echo "FAIL: unsupported $name produced CIL artifact" >&2; exit 1; }
done

# String literal containing "=>" must not trip the Map/Entry gate;
# ordinary string expressions remain rejected by the scalar checker.
set +e
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/map-entry/string-pseudocode.atto "$work/string.dll" >"$work/string.out" 2>"$work/string.err"
status=$?
set -e
test "$status" -eq 2 || { echo "FAIL: unsupported string accepted" >&2; exit 1; }
grep -Fq 'E_NAME:' "$work/string.err"
if grep -Fq 'E_UNSUPPORTED_MAP_ENTRY:' "$work/string.err"; then
  echo "FAIL: string text was mistaken for an Entry operator" >&2; exit 1
fi
test ! -e "$work/string.dll"

# Comments mentioning Map/Entry/=> must not affect a valid scalar guest.
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/map-entry/comment-pseudocode.atto "$work/comment.dll"
test -s "$work/comment.dll"
echo "ATTO_CIL_MAP_ENTRY_ADMISSION_HOST_PASS unsupported=2 string_isolation=1 comment_isolation=1"
