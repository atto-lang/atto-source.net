#!/usr/bin/env bash
# Real source -> direct CIL -> CoreCLR and standalone NativeAOT.
set -euo pipefail
ulimit -c 0
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts
work="$(mktemp -d)"
trap 'rm -rf "$work"; rm -f "$guest"' EXIT
dotnet build "$compiler" -c Release
compile() {
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$1" "$guest"
  test -s "$guest"
}
run_expected() {
  local expected="$1" label="$2"; shift 2
  local rc
  set +e
  "$@" >"$work/stdout" 2>"$work/stderr"
  rc=$?
  set -e
  test "$rc" -eq "$expected" && test ! -s "$work/stdout" && test ! -s "$work/stderr" || {
    cat "$work/stdout" "$work/stderr"; echo "FAIL $label exit=$rc"; exit 1;
  }
  echo "SEQ_EXECUTED $label exit=$rc"
}
for name in seq-nested seq-call-args seq-singleton seq-pipeline-tail seq-pipeline-result seq-bare-return; do
  compile "fixtures/cil/$name.atto"
  replay="$work/$name.dll"
  dotnet run --no-build -c Release --project "$compiler" -- "fixtures/cil/$name.atto" "$replay"
  cmp "$guest" "$replay"
  dotnet build "$host" -c Release -t:Rebuild
  run_expected 42 "$name-CoreCLR" dotnet run --no-build -c Release --project "$host"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$work/native-$name"
  run_expected 42 "$name-NativeAOT" "$work/native-$name/Atto.Cil.Guest.Host"
done
reject() {
  local src="$1" code="$2" rc
  local out="$work/rejected.dll"
  rm -f "$out"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$src" "$out" >"$work/stdout" 2>"$work/stderr"
  rc=$?
  set -e
  test "$rc" -eq 2 && grep -Fq "$code:" "$work/stderr" && test ! -e "$out" || {
    cat "$work/stderr"; echo "SEQ_REJECTION_FAILED $src rc=$rc"; exit 1;
  }
  echo "SEQ_REJECTED $src $code"
}
for entry in seq-empty:E_SEQ_EMPTY seq-leading:E_SEQ_COMMA seq-trailing:E_SEQ_COMMA seq-semicolon:E_PARSE seq-tuple:E_PARSE seq-too-many:E_LIMIT seq-bare-empty:E_SEQ_EMPTY seq-bare-trailing:E_SEQ_COMMA seq-bare-argument:E_PARSE; do
  reject "fixtures/cil/negative/${entry%%:*}.atto" "${entry#*:}"
done
reject fixtures/cil/seq-prefix-divzero.atto E_I64_DIVISION_BY_ZERO
reject fixtures/cil/negative/seq-bare-prefix-divzero.atto E_I64_DIVISION_BY_ZERO
printf 'pub fnc main(): int { return seq(1 > 0, 42); }\n' >"$work/seq-bool.atto"
reject "$work/seq-bool.atto" E_TYPE
printf 'pub fnc main(): int { return seq(missing, 42); }\n' >"$work/seq-missing.atto"
reject "$work/seq-missing.atto" E_SYMBOL
compile fixtures/cil/seq-prefix-overflow.atto
dotnet build "$host" -c Release -t:Rebuild
set +e
dotnet run --no-build -c Release --project "$host" >"$work/stdout" 2>"$work/stderr"
rc=$?
set -e
test "$rc" -ne 0 && test "$rc" -ne 42 && test ! -s "$work/stdout" && grep -Fq OverflowException "$work/stderr"
echo 'SEQ_PREFIX_OVERFLOW_CORECLR_PASS'
echo 'ATTO_DIRECT_CIL_SEQ_INTEGRATED_PASS positive_sources=6 native_executions=6 managed_executions=6 negative_sources=13 prefix_overflow_coreclr=1'
