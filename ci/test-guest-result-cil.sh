#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
source=tests/fixtures/guest-result-cil
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

dotnet build "$compiler" -c Release
oresnet() { dotnet run --project "$compiler" -c Release --no-build -- "$@"; }

for case in arithmetic:0:42 throw-identity:0:42 option-payload:0:42 callable-and-text-error:0:42 \
    'panic-bypasses-trap:70:Oreslang panic' 'raise-bypasses-trap:70:raise bypasses trap' \
    'unhandled-division:70:Oreslang division by zero'; do
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
  if [[ "$status" != "$expected_status" ]]; then
    cat "$output/stdout" "$output/stderr" >&2
    echo "Unexpected guest exit $status for $name" >&2
    exit 1
  fi
  if [[ "$expected_status" == 0 ]]; then
    [[ "$(cat "$output/stdout")" == "$expected_text" && ! -s "$output/stderr" ]]
  else
    [[ ! -s "$output/stdout" ]] && grep -Fq "$expected_text" "$output/stderr"
  fi
  printf 'GUEST_RESULT_CIL %s status=%s\n' "$name" "$status"
done

for case in 'reject-trap-option:Local initializer has wrong type' \
    'reject-throw-int:throw requires Error' \
    'reject-propagation-mismatch:Postfix ? requires an enclosing callable'; do
  name="${case%%:*}"
  expected="${case#*:}"
  output="$work/$name"
  mkdir -p "$output"
  status=0
  oresnet compile "$source/$name.atto" --out "$output/OresGenerated.dll" \
    >"$output/stdout" 2>"$output/stderr" || status=$?
  [[ "$status" == 1 && ! -e "$output/OresGenerated.dll" ]]
  grep -Fq "$expected" "$output/stderr"
  printf 'GUEST_RESULT_NO_ARTIFACT %s\n' "$name"
done
