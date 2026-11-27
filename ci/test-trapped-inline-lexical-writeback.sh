#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
backend="${1:-coreclr}"
case "$backend" in coreclr|nativeaot) ;; *) exit 2 ;; esac
compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
dotnet build "$compiler" -c Release
oresnet() { dotnet run --project "$compiler" -c Release --no-build -- "$@"; }
run_guest() {
  local name="$1" source="$2" want_status="$3" want="$4"
  local out="$tmp/$name"
  mkdir -p "$out"
  printf '%s\n' "$source" > "$out/main.atto"
  oresnet check "$out/main.atto"
  oresnet compile "$out/main.atto" --out "$out/OresGenerated.dll"
  local status=0
  if [[ "$backend" == nativeaot ]]; then
    dotnet publish "$host" -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o "$out/app" "-p:GeneratedAssembly=$out/OresGenerated.dll"
    test -x "$out/app/oreslang-app"
    readelf -h "$out/app/oreslang-app" | grep -q ELF64
    env -i PATH=/usr/bin:/bin "$out/app/oreslang-app" >"$out/stdout" 2>"$out/stderr" || status=$?
  else
    dotnet build "$host" -c Release --no-incremental -o "$out/app" -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false "-p:GeneratedAssembly=$out/OresGenerated.dll"
    dotnet "$out/app/oreslang-app.dll" >"$out/stdout" 2>"$out/stderr" || status=$?
  fi
  test "$status" = "$want_status"
  if [[ "$status" == 0 ]]; then
    test "$(cat "$out/stdout")" = "$want"
    test ! -s "$out/stderr"
  else
    test ! -s "$out/stdout"
    grep -F "$want" "$out/stderr"
  fi
  echo "PASS $backend lexical trap guest $name"
}
reject() {
  local name="$1" source="$2" diagnostic="$3"
  local out="$tmp/$name"
  mkdir -p "$out"
  printf '%s\n' "$source" > "$out/main.atto"
  if oresnet compile "$out/main.atto" --out "$out/OresGenerated.dll" > "$out/log" 2>&1; then
    cat "$out/log"; exit 1
  fi
  test ! -e "$out/OresGenerated.dll"
  grep -F "$diagnostic" "$out/log"
  echo "PASS no-artifact $name"
}
run_guest error-writeback 'pub fnc main():int{let int n=0; val Result<int,Error> r=trap! sync task<int>{n=2;if true {throw! new Error("bad");} return 99;};guard r.is_err(),n eq 2 {return 1;} return 42;}' 0 42
run_guest normal-writeback 'pub fnc main():int{let int n=0; val Result<int,Error> r=trap! sync task<int>{n=2;if false {throw! new Error("bad");} return 40;};guard r.is_ok(),n eq 2 {return 1;} return r.unwrap()+n;}' 0 42
run_guest branch-writeback 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{if n eq 0 {n=42;throw! new Error("bad");} else {n=7;return 9;}};guard r.is_err() {return 1;} return n;}' 0 42
run_guest terminal-panic 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{n=2;if true {panic! new Error("terminal");} return 42;};return 1;}' 70 'Oreslang panic: terminal'
run_guest arithmetic-writeback 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{n=n+2;return 40;};guard r.is_ok(),n eq 2 {return 1;} return r.unwrap()+n;}' 0 42
run_guest unwrap-error-writeback 'pub fnc main():int{let int n=0;val Option<int> missing=None;val Result<int,Error> r=trap! sync task<int>{n=n+2;val int x=missing.unwrap();n=n+100;return x;};guard r.is_err(),n eq 2 {return 1;} return n+40;}' 0 42
run_guest unwrap-success-writeback 'pub fnc main():int{let int n=0;val Option<int> present=Some(40);val Result<int,Error> r=trap! sync task<int>{n=n+2;val int x=present.unwrap();return x;};guard r.is_ok(),n eq 2 {return 1;} return r.unwrap()+n;}' 0 42
run_guest failed-assignment 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{n=42;n=9223372036854775807+1;return 0;};guard r.is_err() {return 1;} return n;}' 0 42
run_guest zero-division 'pub fnc main():int{let int n=0;let int zero=0;val Result<int,Error> r=trap! sync task<int>{n=42;return 7/zero;};guard r.is_err() {return 1;} return n;}' 0 42
run_guest failed-return 'pub fnc main():int{let int n=0;val Option<int> missing=None;val Result<int,Error> r=trap! sync task<int>{n=42;return missing.unwrap();};guard r.is_err(){return 1;}return n;}' 0 42
reject effectful-condition 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{if n+2 eq 2 {return 42;}return 0;};return 0;}' E_TRAP_INLINE_TASK_UNSUPPORTED
reject nested 'pub fnc main():int{val Result<int,Error> r=trap! sync task<int>{return sync task<int>{return 42;};};return 0;}' E_TRAP_INLINE_TASK_UNSUPPORTED
reject cleanup 'pub fnc main():int{val Result<int,Error> r=trap! sync task<int>{defer stdio.stdout.write("D");return 42;};return 0;}' E_LEXICAL_CLEANUP_UNSUPPORTED
reject helper 'fnc f():int{return 42;}pub fnc main():int{val Result<int,Error> r=trap! sync task<int>{return f();};return 0;}' E_TRAP_INLINE_TASK_UNSUPPORTED

run_guest defer-lifo 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{defer n=42;defer n=7;return n;};guard r.is_ok(),n eq 42,r.unwrap() eq 0 {return 1;}return n;}' 0 42
run_guest defer-unwrap 'pub fnc main():int{let int n=0;val Option<int> missing=None;val Result<int,Error> r=trap! sync task<int>{n=2;defer n=42;val int v=missing.unwrap();n=100;return v;};guard r.is_err(){return 1;}return n;}' 0 42
run_guest defer-explicit-throw 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{defer n=42;if true {throw! new Error("bad");}return 0;};guard r.is_err(){return 1;}return n;}' 0 42
run_guest defer-registration-order 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{defer n=42;if true {throw! new Error("bad");}defer n=7;return 0;};guard r.is_err(){return 1;}return n;}' 0 42
run_guest defer-read-at-exit 'pub fnc main():int{let int n=0;let int v=0;val Result<int,Error> r=trap! sync task<int>{defer n=v;v=42;return 0;};return n;}' 0 42
reject deferred-checked 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{defer n=n+1;return 42;};return n;}' E_LEXICAL_CLEANUP_UNSUPPORTED
reject deferred-branch 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{if true {defer n=42;}return 42;};return n;}' E_LEXICAL_CLEANUP_UNSUPPORTED
reject deferred-panic 'pub fnc main():int{let int n=0;val Result<int,Error> r=trap! sync task<int>{defer n=42;if true {panic! new Error("bad");}return 42;};return n;}' E_LEXICAL_CLEANUP_UNSUPPORTED
