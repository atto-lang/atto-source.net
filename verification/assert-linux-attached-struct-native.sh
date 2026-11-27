#!/usr/bin/env bash
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
project=verification/Ores.Net.AttachedStructFixtures/Ores.Net.AttachedStructFixtures.csproj
dotnet build "$project" -c Release
source=verification/Ores.Net.AttachedStructFixtures/nested.atto
compile(){ dotnet run --no-build --project "$project" -c Release -- "$1" "$2"; }
for fixture in nested:42 mutable:1316 ordered:1336; do
  source=verification/Ores.Net.AttachedStructFixtures/${fixture%:*}.atto
  expected=${fixture#*:}
  rm -rf "$work/published"
compile "$source" "$work/OresGenerated.dll"
compile "$source" "$work/repro.dll"
cmp "$work/OresGenerated.dll" "$work/repro.dll"
# Real CLR execution of emitted CIL through a statically referenced observer.
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
dotnet clean "$host" -c Release -r linux-x64 "-p:GeneratedAssembly=$work/OresGenerated.dll"
dotnet publish "$host" -c Release -r linux-x64 -o "$work/published" "-p:GeneratedAssembly=$work/OresGenerated.dll" -warnaserror
app="$work/published/oreslang-app"
test -x "$app"
file "$app" | grep -q 'ELF 64-bit'
test ! -e "$work/published/OresGenerated.dll"
test ! -e "$work/published/oreslang-app.runtimeconfig.json"
test ! -e "$work/published/oreslang-app.deps.json"
test "$("$app")" = "$expected"
echo "NET_ATTACHED_STRUCT_NATIVE_AOT_PASS $source result=$expected"

done

for input in verification/Ores.Net.AttachedStructFixtures/negative/*.atto; do
  name="$(basename "$input" .atto)"
  set +e
  compile "$input" "$work/$name.dll" >"$work/$name.log" 2>&1
  status=$?
  set -e
  test "$status" = 1
  test ! -e "$work/$name.dll"
  grep -Eq 'E_(TYPE|STRUCT_INIT|STRUCT_SYNTAX|DUPLICATE|BORROW_MUT_REQUIRED)' "$work/$name.log"
  echo "NET_ATTACHED_STRUCT_REJECT $name"
done
