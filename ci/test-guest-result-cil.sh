#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source ci/test-guest-result-cases.sh

compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
source=tests/fixtures/guest-result-cil
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

dotnet build "$compiler" -c Release
oresnet() { dotnet run --project "$compiler" -c Release --no-build -- "$@"; }

for case in "${guest_result_cases[@]}"; do
  IFS=: read -r name expected_status expected_text <<< "$case"
  output="$work/$name"
  mkdir -p "$output"
  oresnet check "$source/$name.atto" | grep -F 'verified CFG'
  oresnet compile "$source/$name.atto" --out "$output/OresGenerated.dll"
  dotnet build "$host" -c Release --no-incremental -o "$output/app" \
    -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false \
    "-p:GeneratedAssembly=$output/OresGenerated.dll"
  status=0
  dotnet "$output/app/oreslang-app.dll" >"$output/stdout" 2>"$output/stderr" || status=$?
  assert_guest_result "$name" "$expected_status" "$expected_text" "$status" "$output"
  printf 'GUEST_RESULT_CIL %s status=%s\n' "$name" "$status"
done

for case in "${guest_result_rejections[@]}"; do
  name="${case%%:*}"
  expected="${case#*:}"
  output="$work/$name"
  mkdir -p "$output"
  status=0
  oresnet check "$source/$name.atto" >"$output/check.stdout" 2>"$output/check.stderr" || status=$?
  [[ "$status" == 1 ]]
  grep -Fq "$expected" "$output/check.stderr"
  status=0
  oresnet compile "$source/$name.atto" --out "$output/OresGenerated.dll" \
    >"$output/stdout" 2>"$output/stderr" || status=$?
  [[ "$status" == 1 && ! -e "$output/OresGenerated.dll" ]]
  grep -Fq "$expected" "$output/stderr"
  printf 'GUEST_RESULT_NO_ARTIFACT %s\n' "$name"
done
