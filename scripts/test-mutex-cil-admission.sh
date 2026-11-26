#!/usr/bin/env bash
# Resource constructor syntax admission only, not runtime Mutex support.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dotnet build "$compiler" -c Release
reject() {
  local src="$1" diagnostic="$2" rc
  local artifact="$work/reject.dll"
  rm -f "$artifact"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$src" "$artifact" >"$work/stdout" 2>"$work/stderr"
  rc=$?
  set -e
  test "$rc" -eq 2 && grep -Fq "$diagnostic:" "$work/stderr" && test ! -e "$artifact" && test ! -s "$work/stdout" || {
    cat "$work/stderr"; echo "MUTEX_REJECT_FAILED $src rc=$rc"; exit 1;
  }
  echo "MUTEX_REJECTED $diagnostic $src"
}
reject fixtures/cil/negative/mutex-old-constructor.atto E_NEW_REQUIRED
reject fixtures/cil/negative/mutex-new-unsupported.atto E_LOCK_UNSUPPORTED
printf 'pub fnc main(): int { val m = Mutex<Mutex<int>>(0); return 0; }\n' >"$work/nested-old.atto"
reject "$work/nested-old.atto" E_NEW_REQUIRED
printf 'pub fnc main(): int { val m = new Mutex<Mutex<int>>(0); return 0; }\n' >"$work/nested-new.atto"
reject "$work/nested-new.atto" E_LOCK_UNSUPPORTED
printf 'pub fnc main(): int { return 42; } // Mutex<int>(0)\n' >"$work/comment.atto"
dotnet run --no-build -c Release --project "$compiler" -- "$work/comment.atto" "$work/comment.dll"
test -s "$work/comment.dll"
echo MUTEX_COMMENT_NON_TOKEN_PASS
echo ATTO_MUTEX_CIL_ADMISSION_PASS
