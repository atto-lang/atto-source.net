#!/usr/bin/env bash
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Value.Host/Atto.Cil.Value.Host.csproj
guest=artifacts/AttoGuest.dll
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p artifacts
exact=fixtures/value-loop-recur-v1/230-stateless-value-loop.atto
test "$(git hash-object "$exact")" = 2c0359a6d2876958d6fb6831eb0dac3c6548c15b
dotnet build "$compiler" -c Release
printf '%s\n' 'pub fnc main(): int { return 100 + loop { break 9223372036854775707; }; }' > "$work/full.atto"
printf '%s\n' 'pub fnc main(): int { return loop { break 20+22; }; }' > "$work/arithmetic.atto"
for spec in "$exact:42" "$work/full.atto:9223372036854775807" "$work/arithmetic.atto:42"; do
  path="${spec%:*}"; expected="${spec##*:}"
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$path" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  dotnet run --no-build -c Release --project "$host" > "$work/out" 2>"$work/err"
  test "$(cat "$work/out")" = "$expected"
  test ! -s "$work/err"
  dest="$work/aot-$(basename "$path" .atto)"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$dest"
  "$dest/Atto.Cil.Value.Host" > "$work/out" 2>"$work/err"
  test "$(cat "$work/out")" = "$expected"
  test ! -s "$work/err"
  echo "PASS REAL CIL AND NATIVEAOT $path result=$expected"
done
for expr in 'loop { break; }' 'loop { break 42; break 1; }' 'loop { break f; }' 'loop { break 42; }(1)' 'loop { break f(42); }'; do
  printf 'fnc f(int n): int { return n; } pub fnc main(): int { return %s; }\n' "$expr" > "$work/bad.atto"
  rm -f "$work/bad.dll"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$work/bad.atto" "$work/bad.dll" > "$work/out" 2>"$work/err"
  rc=$?
  set -e
  test "$rc" -eq 2
  test ! -e "$work/bad.dll"
  test -s "$work/err"
  echo "PASS NO ARTIFACT $expr"
done
