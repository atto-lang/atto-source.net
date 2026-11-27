#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mode="${1:-coreclr}"
case "$mode" in coreclr|nativeaot) ;; *) exit 2 ;; esac
source=tests/fixtures/task-inline-cil/exact-guard-trap-sync-task-defer.atto
test "$(git hash-object "$source")" = bcd5e2692aaaaf4c4c070a5746cd6c3ab9e5082a
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
dotnet build "$compiler" -c Release
oresnet() { dotnet run --project "$compiler" -c Release --no-build -- "$@"; }
deny() {
  local source="$1" diagnostic="$2"; shift 2
  local status=0
  oresnet check "$source" "$@" > "$work/check.log" 2>&1 || status=$?
  if [[ "$status" != 1 ]]; then cat "$source" "$work/check.log" >&2; echo "expected check rejection, got $status" >&2; return 1; fi
  grep -F "$diagnostic" "$work/check.log"
  status=0
  oresnet compile "$source" --out "$work/Rejected/OresGenerated.dll" "$@" > "$work/denied.log" 2>&1 || status=$?
  if [[ "$status" != 1 ]]; then cat "$source" "$work/denied.log" >&2; echo "expected compile rejection, got $status" >&2; return 1; fi
  grep -F "$diagnostic" "$work/denied.log"
  test ! -e "$work/Rejected/OresGenerated.dll"
}
mkdir "$work/Rejected"
deny "$source" E_PERMISSION
sed 's/stdio[.]out[.]write/process.stdout.write/g' "$source" > "$work/process-alias.atto"
test "$(git hash-object "$work/process-alias.atto")" = c511c17818f725c9fc5d52a129ca257790276477
deny "$work/process-alias.atto" E_PERMISSION
mixed=tests/fixtures/task-inline-cil/canonical-stdout-v1-mixed-trapped-task.atto
test "$(git hash-object "$mixed")" = 5b122c0396240cee7d0be125f952655560b1e78d
deny "$mixed" E_PERMISSION
for variant in 'stdio.out.write(42)' 'process.stdout.write(42)' 'stdio.stderr.write("D")' 'process.stderr.write("D")' 'stdio.out.write("D", "X")' 'process.stdout.write("D", "X")' 'stdio.out.write("\n")' 'process.stdout.write("\n")' 'stdio.stdout.write("D")'; do
  printf 'pub fnc main():int{val Result<int,Error> r=trap! sync task<int>{defer %s;return 42;};return 42;}\n' "$variant" > "$work/reject.atto"
  echo "Rejecting $variant"
  # Each must be a semantic source failure; an infrastructure error is not proof.
  deny "$work/reject.atto" 'offset' --capability stdio.out.write
done
for source in "$source" "$work/process-alias.atto" "$mixed"; do
expected=D
if [[ "$source" == "$mixed" ]]; then expected=ED; fi
echo "Compiling exact granted fixture"
oresnet check "$source" --capability stdio.out.write
oresnet compile "$source" --out "$work/OresGenerated.dll" --capability stdio.out.write
if [[ "$mode" == nativeaot ]]; then
  dotnet publish "$host" -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o "$work/app" "-p:GeneratedAssembly=$work/OresGenerated.dll"
  readelf -h "$work/app/oreslang-app" | grep -q ELF64
  runner=(env -i PATH=/usr/bin:/bin "$work/app/oreslang-app")
else
  dotnet build "$host" -c Release --no-incremental -o "$work/app" -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false "-p:GeneratedAssembly=$work/OresGenerated.dll"
  runner=(dotnet "$work/app/oreslang-app.dll")
fi
status=0
"${runner[@]}" > "$work/stdout" 2> "$work/stderr" || status=$?
test "$status" = 70
test ! -s "$work/stdout"
grep -F 'deferred stdout denied or quota exceeded' "$work/stderr"
"${runner[@]}" --allow-deferred-stdout > "$work/stdout" 2> "$work/stderr"
printf '%s42\n' "$expected" > "$work/expected"
cmp "$work/expected" "$work/stdout"
test ! -s "$work/stderr"
echo "PASS CANONICAL_GUARD_TRAP_DEFER $mode source=$source stdout=${expected}42 compiler_grant=yes runtime_grant=yes"
rm -rf -- "$work/app" "$work/OresGenerated.dll"
done
