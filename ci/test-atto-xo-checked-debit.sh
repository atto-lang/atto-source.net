#!/usr/bin/env bash
# Execute the pinned shared corpus through emitted CIL or a statically linked
# native application. A host oracle or a compiler failure is not a guest pass.
set -euo pipefail
cd "$(dirname "$0")/.."
mode="${1:-}"
[[ "$mode" == coreclr || "$mode" == native-macos ]] || {
  echo 'usage: bash ci/test-atto-xo-checked-debit.sh coreclr|native-macos' >&2
  exit 2
}
corpus=contracts/atto-xo-checked-debit-v1/cases.tsv
[[ "$(git hash-object "$corpus")" == 6831dbd0c18eeaa2078fa5f3e324d0f88b40771e ]]
work="$(mktemp -d "$PWD/.atto-xo-debit.XXXXXX")"
suffix="${work##*.}"
trap 'rm -rf -- "$work"' EXIT
compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
if [[ "$mode" == coreclr ]]; then
  dotnet build "$compiler" -c Release
else
  dotnet restore "$host" -r osx-arm64
fi
value_count=0
overflow_count=0
while IFS='|' read -r id expected price shares fee op adjustment guest_source; do
  [[ "$id" =~ ^[a-z0-9-]+$ && -n "$guest_source" ]]
  output="$work/$id"
  mkdir "$output"
  input="$output/main.atto"
  printf '%s\n' "$guest_source" >"$input"
  status=0
  if [[ "$mode" == coreclr ]]; then
    dotnet run --project "$compiler" -c Release --no-build -- check "$input" |
      grep -F 'verified CFG'
    dotnet run --project "$compiler" -c Release --no-build -- compile "$input" \
      --out "$output/OresGenerated.dll"
    [[ -s "$output/OresGenerated.dll" ]]
    dotnet build "$host" -c Release --no-incremental -o "$output/app" \
      -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false \
      "-p:GeneratedAssembly=$output/OresGenerated.dll"
    dotnet "$output/app/oreslang-app.dll" >"$output/stdout" 2>"$output/stderr" || status=$?
  else
    destination="artifacts/atto-xo-debit-$suffix-$id"
    bash scripts/publish-macos.sh "$input" "$destination"
    app="$destination/osx-arm64/oreslang-app"
    bash verification/assert-macos-native-release.sh "$app"
    env -i PATH=/usr/bin:/bin "$app" >"$output/stdout" 2>"$output/stderr" || status=$?
  fi
  if [[ "$expected" == overflow ]]; then
    [[ "$status" == 70 && ! -s "$output/stdout" ]]
    printf '%s\n' 'Oreslang arithmetic overflow' >"$output/expected"
    cmp "$output/expected" "$output/stderr"
    overflow_count=$((overflow_count + 1))
  else
    [[ "$expected" == 42 || "$expected" == 43 ]]
    [[ "$status" == 0 && ! -s "$output/stderr" ]]
    printf '%s\n' "$expected" >"$output/expected"
    cmp "$output/expected" "$output/stdout"
    value_count=$((value_count + 1))
  fi
  printf 'ATTO_XO_DEBIT_GUEST mode=%s id=%s status=%s\n' "$mode" "$id" "$status"
done <"$corpus"
[[ "$value_count" == 3 && "$overflow_count" == 4 ]]
printf 'ATTO_XO_DEBIT_GUEST_PASS mode=%s values=%s overflow=%s\n' "$mode" "$value_count" "$overflow_count"
