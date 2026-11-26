#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
profile=${1:?expected coreclr or macos-arm64}
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
project=compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
python3 verification/frozen-package-cli-fixtures.py "$work"
pin="$(cat "$work/pin")"
case "$profile" in
  coreclr)
    dotnet build "$project" -c Release
    cli=(dotnet run --project "$project" -c Release --no-build --)
    ;;
  macos-arm64)
    [[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]]
    dotnet publish "$project" -c Release -r osx-arm64 --self-contained true -p:PublishAot=true -o "$work/compiler" -warnaserror
    bash verification/assert-macos-native-release.sh "$work/compiler/oresnet"
    cli=("$work/compiler/oresnet")
    ;;
  *) exit 2 ;;
esac
generated="$work/OresGenerated.dll"
"${cli[@]}" check-frozen "$work/main.atto" --package "$work/good.zip" @org/project/package 1.0.0 "$pin"
"${cli[@]}" compile-frozen "$work/main.atto" --package "$work/good.zip" @org/project/package 1.0.0 "$pin" --out "$generated"
test -s "$generated"
cp "$generated" "$work/baseline.dll"
for pair in guard-provider:E_UNSUPPORTED match-provider:E_UNSUPPORTED sync-provider:E_LINKED_SYNC_TASK_UNSUPPORTED suspends-provider:E_SUSPENDS_UNSUPPORTED trap-provider:E_LINKED_GUEST_EFFECT_UNSUPPORTED; do
  fixture=${pair%%:*}
  expected=${pair#*:}
  for action in check-frozen compile-frozen; do
    args=("$action" "$work/main.atto" --package "$work/$fixture.zip" @org/project/package 1.0.0 "$(cat "$work/$fixture.pin")")
    if [[ "$action" == compile-frozen ]]; then args+=(--out "$generated"); fi
    status=0
    "${cli[@]}" "${args[@]}" >"$work/stdout" 2>"$work/stderr" || status=$?
    if [[ "$status" != 1 ]] || ! grep -Fq "$expected" "$work/stderr"; then
      cat "$work/stdout" "$work/stderr" >&2
      echo "FROZEN_PROVIDER_DENIAL_MISMATCH $fixture $action status=$status expected=$expected" >&2
      exit 1
    fi
    cmp "$generated" "$work/baseline.dll"
  done
  echo "FROZEN_PROVIDER_DENIED $profile $fixture $expected"
done
if [[ "$profile" == macos-arm64 ]]; then
  dotnet clean "$host" -c Release -r osx-arm64 "-p:GeneratedAssembly=$generated"
  dotnet publish "$host" -c Release -r osx-arm64 --self-contained true -p:PublishAot=true -o "$work/app" "-p:GeneratedAssembly=$generated" -warnaserror
  bash verification/assert-macos-native-release.sh "$work/app/oreslang-app"
  runner=(env -i PATH=/usr/bin:/bin "$work/app/oreslang-app")
else
  dotnet build "$host" -c Release --no-incremental -o "$work/app" -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false "-p:GeneratedAssembly=$generated"
  runner=(dotnet "$work/app/oreslang-app.dll")
fi
"${runner[@]}" >"$work/stdout" 2>"$work/stderr"
[[ "$(cat "$work/stdout")" == 42 && ! -s "$work/stderr" ]]
printf 'FROZEN_TASK_INTEGRATION_GUEST %s value=42 cil_blob=%s\n' "$profile" "$(git hash-object "$generated")"
