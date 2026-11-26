#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
source=examples/unicode-identifiers-nativeaot.atto
test "$(git hash-object --no-filters "$source")" = "$(git rev-parse "HEAD:$source")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
generated="$work/OresGenerated.dll"
dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -- compile-structured "$source" --out "$generated"
test -s "$generated"
sha256sum "$generated"
dotnet publish host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj -c Release -r linux-x64 -o "$work/published" "-p:GeneratedAssembly=$generated" -warnaserror
app="$work/published/oreslang-app"
test -x "$app"
file "$app" | grep -q 'ELF 64-bit'
test ! -e "$work/published/OresGenerated.dll"
test ! -e "$work/published/oreslang-app.runtimeconfig.json"
result="$(timeout 20 "$app")"
test "$result" = 100
sha256sum "$app"
echo "UNICODE_NATIVEAOT_GUEST_RESULT=$result"
