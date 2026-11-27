#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
profile="${1:-cil}"
compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
fixture=fixtures/guard-condition-list-v1/trap-sync-task-defer.atto
test "$(git hash-object "$fixture")" = bcd5e2692aaaaf4c4c070a5746cd6c3ab9e5082a
dotnet build "$compiler" -c Release
oresnet() { dotnet run --project "$compiler" -c Release --no-build -- "$@"; }
deny() {
  local name="$1" input="$2" code="$3"; shift 3
  local out="$work/$name/OresGenerated.dll"
  mkdir -p "$(dirname "$out")"
  for mode in check compile; do
    args=("$mode" "$input")
    if [[ "$mode" == compile ]]; then args+=(--out "$out"); fi
    if oresnet "${args[@]}" "$@" >"$work/deny.log" 2>&1; then cat "$work/deny.log"; exit 1; fi
    grep -F "$code" "$work/deny.log"
    test ! -e "$out"
  done
  echo "PASS compiler denial $name"
}
run() {
  local name="$1" input="$2" expected="$3"
  local out="$work/$name"
  mkdir -p "$out"
  oresnet check "$input" --allow-stdio
  oresnet compile "$input" --out "$out/OresGenerated.dll" --allow-stdio
  if [[ "$profile" == native ]]; then
    dotnet publish "$host" -c Release -r linux-x64 -o "$out/app" -p:PublishAot=true -p:SelfContained=true "-p:GeneratedAssembly=$out/OresGenerated.dll"
    readelf -h "$out/app/oreslang-app" | grep -q ELF64
    command=("$out/app/oreslang-app")
  else
    dotnet build "$host" -c Release --no-incremental -o "$out/app" -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false "-p:GeneratedAssembly=$out/OresGenerated.dll"
    command=(dotnet "$out/app/oreslang-app.dll")
  fi
  local status=0
  "${command[@]}" >"$out/denied.stdout" 2>"$out/denied.stderr" || status=$?
  if [[ "$name" != before-registration ]]; then
    test "$status" = 70
    test ! -s "$out/denied.stdout"
    grep -F 'E_PERMISSION: root stdout grant or budget denied' "$out/denied.stderr"
  else
    test "$status" = 0
    printf '42\n' >"$out/expected"
    cmp "$out/expected" "$out/denied.stdout"
    test ! -s "$out/denied.stderr"
  fi
  "${command[@]}" --allow-stdio >"$out/stdout" 2>"$out/stderr"
  printf '%s\n' "$expected" >"$out/expected"
  cmp "$out/expected" "$out/stdout"
  test ! -s "$out/stderr"
  echo "PASS exact stdout $profile $name"
}
deny no-compile-grant "$fixture" E_PERMISSION
run canonical-stdio "$fixture" D42
sed 's/stdio\.out\.write/process.stdout.write/g' "$fixture" > "$work/process-alias.atto"
deny no-process-compile-grant "$work/process-alias.atto" E_PERMISSION
run canonical-process "$work/process-alias.atto" D42
printf '%s\n' 'pub fnc main():int{val Result<int,Error> r=trap! sync task<int>{defer stdio.out.write("A");defer stdio.out.write("B");return 42;};return r.unwrap();}' >"$work/lifo.atto"
run lifo "$work/lifo.atto" BA42
printf '%s\n' 'pub fnc main():int{val Option<int> missing=None;val Result<int,Error> r=trap! sync task<int>{val int x=missing.unwrap();defer stdio.out.write("D");return x;};guard r.is_err(){return 1;}return 42;}' >"$work/before.atto"
# Host grant is dynamically needed only if registration was reached and executed.
run before-registration "$work/before.atto" 42
printf '%s\n' 'pub fnc main():int{val Result<int,Error> r=trap! sync task<int>{defer stdio.out.read("D");return 42;};return r.unwrap();}' >"$work/member.atto"
deny wrong-member "$work/member.atto" "Expected 'write'" --allow-stdio
printf '%s\n' 'pub fnc main():int{val Result<int,Error> r=trap! sync task<int>{defer stdio.out.write(42);return 42;};return r.unwrap();}' >"$work/type.atto"
deny nonliteral "$work/type.atto" "ordinary literal message" --allow-stdio
