#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
fixtures=tests/fixtures/guard-dual-form-v1
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT

dotnet build "$compiler" -c Release
compile() {
  dotnet run --project "$compiler" -c Release --no-build --     compile-structured "$fixtures/$1.atto" --out "$2"
}

for case in brace-continue:42 keyword-continue:42 keyword-failure:0 short-circuit:42; do
  fixture="${case%%:*}"
  expected="${case#*:}"
  output="$tmp/$fixture"
  mkdir -p "$output"
  compile "$fixture" "$output/OresGenerated.dll"
  dotnet build "$host" -c Release --no-incremental -o "$output/app"     -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false     "-p:GeneratedAssembly=$output/OresGenerated.dll"
  actual="$(dotnet "$output/app/oreslang-app.dll")"
  if [[ "$actual" != "$expected" ]]; then
    printf 'Unexpected guard result for %s (expected %s, got %s)\n'       "$fixture" "$expected" "$actual" >&2
    exit 1
  fi
  printf 'CoreCLR structured guard %s = %s\n' "$fixture" "$actual"
done

for case in reject-fallthrough:E_GUARD reject-mixed:E_GUARD reject-nonbool:E_GUARD             "reject-infix-caret:Reserved AttoLang operator '^'"; do
  fixture="${case%%:*}"
  diagnostic="${case#*:}"
  rejected="$tmp/OresGenerated.dll"
  status=0
  compile "$fixture" "$rejected" > "$tmp/stdout" 2> "$tmp/stderr" || status=$?
  if [[ "$status" != 1 || -e "$rejected" ]] ||
      ! grep -Fq "$diagnostic" "$tmp/stderr"; then
    cat "$tmp/stderr" >&2
    printf 'Unexpected guard rejection for %s (exit %s)\n' "$fixture" "$status" >&2
    exit 1
  fi
done
echo "Structured guard CoreCLR and negative admission checks passed."
