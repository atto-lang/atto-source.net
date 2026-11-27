#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
profile=${1:?expected coreclr or macos-arm64}
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
project=verification/RootTaskLifecycle/RootTaskLifecycle.csproj
case "$profile" in
  coreclr)
    dotnet build "$project" -c Release -o "$work/app"
    runner=(dotnet "$work/app/root-task-lifecycle.dll")
    ;;
  macos-arm64)
    [[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]]
    dotnet publish "$project" -c Release -r osx-arm64 --self-contained true \
      -p:PublishAot=true -o "$work/app" -warnaserror
    bash verification/assert-macos-native-release.sh "$work/app/root-task-lifecycle"
    runner=(env -i PATH=/usr/bin:/bin "$work/app/root-task-lifecycle")
    ;;
  *) exit 2 ;;
esac
"${runner[@]}" >"$work/stdout" 2>"$work/stderr" || { cat "$work/stdout" "$work/stderr" >&2; exit 1; }
[[ "$(cat "$work/stdout")" == ROOT_TASK_LIFECYCLE_HOST_PASS && ! -s "$work/stderr" ]]
printf 'ROOT_TASK_LIFECYCLE_HOST_PASS %s test_blob=%s\n' "$profile" \
  "$(git hash-object verification/Ores.Net.Tests/RootTaskI64Tests.cs)"
