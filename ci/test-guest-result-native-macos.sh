#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source ci/test-guest-result-cases.sh
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

dotnet restore host/Ores.Net.NativeHost -r osx-arm64
for case in "${guest_result_cases[@]}"; do
  IFS=: read -r fixture expected_status expected_text <<< "$case"
  destination="artifacts/guest-result-$fixture"
  bash scripts/publish-macos.sh "tests/fixtures/guest-result-cil/$fixture.atto" "$destination"
  app="$destination/osx-arm64/oreslang-app"
  output="$work/$fixture"
  mkdir -p "$output"
  status=0
  env -i PATH=/usr/bin:/bin "$app" >"$output/stdout" 2>"$output/stderr" || status=$?
  assert_guest_result "$fixture" "$expected_status" "$expected_text" "$status" "$output"
  printf 'GUEST_RESULT_NATIVE_AOT %s status=%s\n' "$fixture" "$status"
done

compiler=artifacts/compiler/osx-arm64/oresnet
bash verification/assert-macos-native-release.sh "$compiler"
for case in "${guest_result_rejections[@]}"; do
  name="${case%%:*}"
  expected="${case#*:}"
  source="tests/fixtures/guest-result-cil/$name.atto"
  output="$work/$name"
  mkdir -p "$output"
  status=0
  "$compiler" check "$source" >"$output/check.stdout" 2>"$output/check.stderr" || status=$?
  [[ "$status" == 1 ]]
  grep -Fq "$expected" "$output/check.stderr"
  status=0
  "$compiler" compile "$source" --out "$output/OresGenerated.dll" >"$output/stdout" 2>"$output/stderr" || status=$?
  [[ "$status" == 1 && ! -e "$output/OresGenerated.dll" ]]
  grep -Fq "$expected" "$output/stderr"
  printf 'GUEST_RESULT_NATIVE_NO_ARTIFACT %s\n' "$name"
done
