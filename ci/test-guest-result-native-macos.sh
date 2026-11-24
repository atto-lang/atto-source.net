#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet restore host/Ores.Net.NativeHost -r osx-arm64
for fixture in arithmetic throw-identity option-payload callable-and-text-error panic-bypasses-trap unhandled-remainder \
    match-error match-guarded match-void match-i64; do
  destination="artifacts/guest-result-$fixture"
  bash scripts/publish-macos.sh "tests/fixtures/guest-result-cil/$fixture.atto" "$destination"
  app="$destination/osx-arm64/oreslang-app"
  status=0
  actual="$(env -i PATH=/usr/bin:/bin "$app" 2>&1)" || status=$?
  case "$fixture" in
    panic-bypasses-trap) [[ "$status" == 70 && "$actual" == *'Oreslang panic'* ]] ;;
    unhandled-remainder) [[ "$status" == 70 && "$actual" == *'Oreslang division by zero'* ]] ;;
    match-error) [[ "$status" == 0 && "$actual" == 7 ]] ;;
    *) [[ "$status" == 0 && "$actual" == 42 ]] ;;
  esac
  printf 'GUEST_RESULT_NATIVE_AOT %s status=%s\n' "$fixture" "$status"
done
