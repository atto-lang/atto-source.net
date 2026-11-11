#!/usr/bin/env bash
# Exact guest .atto source -> structured CFG -> build-time CIL -> .NET NativeAOT.
# This is a real target execution gate; unlike host CIL evaluators, it cannot
# be marked passed without a compiled executable running to completion.
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
command -v dotnet >/dev/null || { echo "dotnet SDK missing" >&2; exit 2; }
command -v file >/dev/null || { echo "file command missing" >&2; exit 2; }
work="$(mktemp -d "${TMPDIR:-/tmp}/atto-net-equality.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT
generated="$work/OresGenerated.dll"
source="$root/examples/atto-neq-nativeaot.atto"
negative="$root/examples/atto-neq-mixed-bool-reject.atto"
rejected="$work/rejected/OresGenerated.dll"

dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -- check-structured "$source"
dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -- compile-structured "$source" --out "$generated"
test -s "$generated" || { echo "No guest CIL" >&2; exit 1; }
if dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -- compile-structured "$negative" --out "$rejected"; then
  echo "Mixed-type negative source was unexpectedly compiled" >&2; exit 1
fi
test ! -e "$rejected" || { echo "Rejected source produced a CIL artifact" >&2; exit 1; }

project="host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj"
output="$work/published"
dotnet clean "$project" -c Release -r linux-x64 "-p:GeneratedAssembly=$generated"
dotnet publish "$project" -c Release -r linux-x64 -o "$output" "-p:GeneratedAssembly=$generated" -warnaserror

app="$output/oreslang-app"
test -x "$app" || { echo "Missing NativeAOT guest executable" >&2; exit 1; }
file "$app" | grep -q 'ELF 64-bit' || { echo "Not native ELF" >&2; exit 1; }
test ! -e "$output/oreslang-app.runtimeconfig.json" || { echo "Managed runtimeconfig sidecar not permitted" >&2; exit 1; }
test ! -e "$output/OresGenerated.dll" || { echo "Generated guest DLL must be statically linked" >&2; exit 1; }
set +e
result="$("$app")"
status=$?
set -e
if [[ "$status" -ne 0 || "$result" != '42' ]]; then
  printf 'NativeAOT Atto equality mismatch: exit=%s output=%q (expected 42)\n' "$status" "$result" >&2
  exit 1
fi
echo 'NativeAOT AttoLang eq/neq/!eq guest execution OK: 42'
