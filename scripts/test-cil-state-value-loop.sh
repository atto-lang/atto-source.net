#!/usr/bin/env bash
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Value.Host/Atto.Cil.Value.Host.csproj
guest=artifacts/AttoGuest.dll
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p artifacts
dotnet build "$compiler" -c Release
run_guest() {
  local id="$1" source="$2" expected="$3" mode="$4"
  printf '%s\n' "$source" > "$work/$id.atto"
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$work/$id.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  dotnet run --no-build -c Release --project "$host" > "$work/out" 2>"$work/err"
  test "$(cat "$work/out")" = "$expected"
  test ! -s "$work/err"
  echo "PASS CoreCLR $id $expected"
  if [[ "$mode" == aot ]]; then
    local dest="$work/aot-$id"
    dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$dest"
    test -x "$dest/Atto.Cil.Value.Host"
    readelf -h "$dest/Atto.Cil.Value.Host" | grep -q ELF64
    "$dest/Atto.Cil.Value.Host" >"$work/out" 2>"$work/err"
    test "$(cat "$work/out")" = "$expected"
    test ! -s "$work/err"
    echo "PASS NativeAOT $id $expected"
  fi
}
run_guest sum 'pub fnc main(): int { val total:int = loop (val i:int=0,val sum:int=0) { if i>=10 {break sum;} recur(i+1,sum+i); }; return total; }' 45 aot
run_guest swap 'pub fnc main(): int { return loop (val i=0,val a=2,val b=a+40) { if i eq 1 { break a; } else { recur(i+1,b,a); } }; }' 42 aot
run_guest full 'pub fnc main(): int { return loop (val i=0,val sum=0) { if i>=10000000 { break sum; } recur(i+1,sum+i); }; }' 49999995000000 aot
run_guest arg 'fnc f(int n):int {return n;} pub fnc main():int {return 2+f(loop (val i=0) {if i eq 1 {break 40;} recur(i+1);});}' 42 coreclr
run_guest ordered 'pub fnc main():int {return loop (val i=0) {if i eq 1 {break 42;} if i eq 1 {break 7;} recur(i+1);};}' 42 coreclr
run_guest two 'pub fnc main():int {return loop (val i=0) {if i eq 0 {break 20;} recur(i+1);} + loop (val i=0) {if i eq 0 {break 22;} recur(i+1);};}' 42 coreclr
reject() {
  printf 'pub fnc main():int { return %s; }\n' "$1" > "$work/bad.atto"
  rm -f "$work/bad.dll"
  local rc=0
  dotnet run --no-build -c Release --project "$compiler" -- "$work/bad.atto" "$work/bad.dll" >"$work/out" 2>"$work/err" || rc=$?
  test "$rc" -eq 2
  test ! -e "$work/bad.dll"
  grep -F "$2" "$work/err"
}
reject 'loop (val i=0) {if i eq 1 {break i;} recur(i+1,2);}' E_RECUR_ARITY
reject 'loop (val a=b,val b=42) {if b eq 42 {break a;} recur(a,b);}' E_VALUE_LOOP_PROFILE
reject 'loop (val i=0) {if i eq 1 {break i;} recur(i eq 0);}' E_TYPE
reject 'loop (val i=0) {if i {break i;} recur(i+1);}' E_TYPE
reject 'loop (val i=0) {if i eq 1 {break i;} recur(i+1); recur(i+1);}' E_PARSE
reject 'loop (val i=0) {if i eq 1 {break i;} recur(loop {break 1;});}' E_VALUE_LOOP_PROFILE
