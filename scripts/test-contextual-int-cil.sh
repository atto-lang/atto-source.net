#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
mkdir -p artifacts
guest=artifacts/AttoGuest.dll
work="$(mktemp -d)"
trap 'rm -rf "$work"; rm -f "$guest"' EXIT
dotnet build "$compiler" -c Release
compile() { dotnet run --no-build -c Release --project "$compiler" -- "$1" "$2"; }
program() { printf 'define class Scale as pub static fnc apply::(%s)(int value) -> int { return %s; } end fnc main() -> int { %s }\n' "$1" "$2" "$3"; }
expect_exit() {
  local expected="$1"; shift
  local status=0
  "$@" || status=$?
  test "$status" = "$expected"
}
program 'int factor' 'factor * value' 'return Scale::(6).apply(7);' >"$work/direct.atto"
program 'int factor' 'factor * value' 'const x = Scale::(2); const y = Scale::(3); return x.apply(4) + y.apply(4);' >"$work/reuse.atto"
program 'int factor, int offset' 'factor * value + offset' 'const x = Scale::(2, 3); const y = Scale::(4, 5); return x.apply(7) + y.apply(7);' >"$work/multi.atto"
program 'int factor' 'factor * value' 'const x = Scale::(6); const y = x; return y.apply(7);' >"$work/alias.atto"
for spec in direct:42 reuse:20 multi:50 alias:42; do
  name="${spec%:*}"; expected="${spec#*:}"
  rm -f "$guest"
  compile "$work/$name.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  expect_exit "$expected" dotnet run --no-build -c Release --project "$host"
  echo "PASS CONTEXT_CORECLR $name exit=$expected"
  if [[ "$name" == multi || "$name" == alias ]]; then
    dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$work/native-$name"
    expect_exit "$expected" "$work/native-$name/Atto.Cil.Guest.Host"
    echo "PASS CONTEXT_NATIVEAOT $name exit=$expected"
  fi
done
for body in 'return Scale::(true).apply(7);' 'return Scale::(2,3).apply(7);' 'return Scale::(2).apply();' 'return Scale::(2).other(7);' 'const x = Scale::(2); return x;' 'const x = Scale::(2); const x = Scale::(3); return x.apply(7);' 'return Scale::(factor=2).apply(7);' 'return Scale::(2).apply(7]);' 'return _atto_ctx_v1_member(2,7);'; do
  program 'int factor' 'factor * value' "$body" >"$work/negative.atto"
  rm -f "$work/rejected.dll"
  status=0; compile "$work/negative.atto" "$work/rejected.dll" >"$work/stdout" 2>"$work/stderr" || status=$?
  test "$status" = 2
  grep -Eq 'E_(CONTEXT_PROFILE|LEX):' "$work/stderr"
  test ! -e "$work/rejected.dll"
  echo "PASS CONTEXT_REJECT $body"
done
