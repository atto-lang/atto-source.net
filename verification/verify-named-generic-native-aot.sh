#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/.."
printf 'SOURCE_HEAD=%s SOURCE_TREE=%s SDK=%s\n' "$(git rev-parse HEAD)" "$(git rev-parse HEAD^{tree})" "$(dotnet --version)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
compiler="$work/compiler/oresnet"
host="$PWD/host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj"
dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r linux-x64 -o "$work/compiler" -warnaserror
bash verification/assert-linux-native-release.sh "$compiler"
pre='fnc identity<T>(T value): T { return value; }'
count=0
guest() {
  local name="$1" expr="$2"
  mkdir -p "$work/$name"
  printf '%s\npub fnc main(): int { return %s; }\n' "$pre" "$expr" >"$work/$name.atto"
  local generated="$work/$name/OresGenerated.dll"
  "$compiler" compile "$work/$name.atto" --out "$generated"
  test -s "$generated"
  dotnet clean "$host" -c Release -r linux-x64 "-p:GeneratedAssembly=$generated"
  dotnet publish "$host" -c Release -r linux-x64 -o "$work/$name/native" "-p:GeneratedAssembly=$generated" -warnaserror
  bash verification/assert-linux-native-release.sh "$work/$name/native/oreslang-app"
  timeout --signal=TERM --kill-after=2s 10s "$work/$name/native/oreslang-app" >"$work/stdout" 2>"$work/stderr"
  cmp -s "$work/stdout" <(printf '42\n')
  test ! -s "$work/stderr"
  printf 'NATIVE_AOT_PASS %s\n' "$name"
  count=$((count+1))
}
guest positional 'identity<int>(42)'
guest named 'identity<T=int>(42)'
guest combined 'identity<T=int>(value:42)'
guest inferred 'identity(42)'
guest reuse 'identity<T=int>(20)+identity<int>(22)'
reject() {
  local expr="$1" status=0
  printf '%s\npub fnc main(): int { return %s; }\n' "$pre" "$expr" >"$work/reject.atto"
  rm -f "$work/reject.dll"
  "$compiler" compile "$work/reject.atto" --out "$work/reject.dll" >"$work/stdout" 2>"$work/stderr" || status=$?
  test "$status" -eq 1
  test ! -e "$work/reject.dll"
  test -s "$work/stderr"
  printf 'NATIVE_AOT_REJECT %s\n' "$expr"
  count=$((count+1))
}
for expr in 'identity<U=int>(42)' 'identity<t=int>(42)' 'identity<T=3>(42)' 'identity<T=bool>(true)' 'identity<T=int,T=int>(42)' 'identity[T=int](42)' 'identity<T=int](42)' 'identity<T=int>(true)' 'identity<T=int>(other:42)' 'identity<T=int>(value:42,value:42)'; do
  reject "$expr"
done
test "$count" -eq 15
git diff --quiet -- . && git diff --cached --quiet -- .
printf 'NATIVE_AOT_VERIFIED %s cases\n' "$count"
