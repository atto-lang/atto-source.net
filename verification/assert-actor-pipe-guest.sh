#!/usr/bin/env bash
set -euo pipefail

if [[ $# -gt 1 ]]; then
    echo 'usage: bash verification/assert-actor-pipe-guest.sh [osx-arm64|linux-x64]' >&2
    exit 2
fi
rid=${1:-}
root="$(cd "$(dirname "$0")/.." && pwd -P)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cd "$root"

compiler_project=compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj
host_project=host/Ores.Net.ActorPipeNativeHost/Ores.Net.ActorPipeNativeHost.csproj
if [[ -z "$rid" ]]; then
    dotnet build "$compiler_project" -c Release -o "$work/compiler" -warnaserror
    compiler=(dotnet "$work/compiler/oresnet.dll")
else
    case "$rid" in
        osx-arm64) target_check=verification/assert-macos-native-release.sh ;;
        linux-x64) target_check=verification/assert-linux-native-release.sh ;;
        *) echo "unsupported NativeAOT verification target $rid" >&2; exit 2 ;;
    esac
    dotnet publish "$compiler_project" -c Release -r "$rid" -o "$work/compiler" -warnaserror
    compiler=("$work/compiler/oresnet")
    bash "$target_check" "${compiler[0]}"
fi

fixture=tests/fixtures/actor-pipe-three-stage.atto
for specification in \
    'forward|source pipe! middle pipe sink;' \
    'reverse|sink rpipe middle rpipe! source;' \
    'mixed|sink rpipe! middle pipe source;' \
    'suffix|source pipe!middle pipe sink;' \
    'split|source pipe ! middle pipe sink;'; do
    name=${specification%%|*}
    sed "s/source pipe middle pipe sink;/${specification#*|}/" "$fixture" > "$work/$name.atto"
done
sed -e 's/on_message(int n): int { return n + 1; }/on_message(int n): bool { return n == 20; }/' \
    -e 's/on_message(int n): int { return n \* 2; }/on_message(bool ok): int { if ok { return 42; } return 0; }/' \
    "$fixture" > "$work/typed.atto"
sed 's/source.send(20);/source.send(9223372036854775807);/' "$fixture" > "$work/fault.atto"
sed 's/return n + 1;/while n == 20 { } return n;/' "$fixture" > "$work/loop.atto"

run_guest() {
    local name=$1 source=$2 expected_status=$3 case_dir="$work/$1"
    mkdir "$case_dir"
    local generated="$case_dir/OresGenerated.dll"
    local -a app
    "${compiler[@]}" compile-actor-pipe "$source" --out "$generated"
    if [[ -z "$rid" ]]; then
        dotnet build "$host_project" -t:Rebuild -c Release -p:PublishAot=false -p:SelfContained=false \
            "-p:GeneratedAssembly=$generated" -o "$case_dir/host" -warnaserror
        app=(dotnet "$case_dir/host/oreslang-actor-pipe.dll")
    else
        dotnet clean "$host_project" -c Release -r "$rid" "-p:GeneratedAssembly=$generated"
        dotnet publish "$host_project" -c Release -r "$rid" "-p:GeneratedAssembly=$generated" \
            -o "$case_dir/host" -warnaserror
        app=("$case_dir/host/oreslang-actor-pipe")
        bash "$target_check" "${app[0]}"
        [[ ! -e "$case_dir/host/OresGenerated.dll" ]]
    fi
    local status=0
    "${app[@]}" > "$case_dir/result" 2> "$case_dir/receipt" || status=$?
    [[ "$status" == "$expected_status" ]]
    if [[ "$status" == 0 ]]; then
        [[ "$(cat "$case_dir/result")" == 42 ]]
        grep -Fx 'ACTOR_PIPE_RECEIPT; turns=3; live=0; native_bytes=0' "$case_dir/receipt"
    else
        [[ ! -s "$case_dir/result" ]]
        grep -Fx 'E_ACTOR_PIPE_TURN_FAULT: a pipeline stage failed' "$case_dir/receipt"
    fi
    printf 'PASS %s status=%s git_blob=%s\n' "$name" "$status" "$(git hash-object "$source")"
}

reject() {
    local name=$1 diagnostic=$2
    local denied="$work/$name.dll"
    if "${compiler[@]}" compile-actor-pipe "$work/$name.atto" --out "$denied" \
        > "$work/rejection" 2>&1; then
        echo "unexpectedly compiled $name" >&2
        exit 1
    fi
    [[ ! -e "$denied" ]]
    grep -F "$diagnostic" "$work/rejection"
    printf 'PASS %s rejected without artifact git_blob=%s\n' "$name" "$(git hash-object "$work/$name.atto")"
}

reject mixed E_ACTOR_PIPE_PLAN_V1
reject suffix E_PIPE_BANG_BOUNDARY
reject split 'expected bounded ASCII identifier'
reject loop E_ACTOR_PIPE_TURN_UNBOUNDED
run_guest bare "$fixture" 0
run_guest forward "$work/forward.atto" 0
run_guest reverse "$work/reverse.atto" 0
run_guest typed "$work/typed.atto" 0
run_guest fault "$work/fault.atto" 70
