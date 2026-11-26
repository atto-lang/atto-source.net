#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
profile=${1:?expected coreclr or macos-arm64}
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
project=compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj
host=host/Ores.Net.TaskI64NativeHost/Ores.Net.TaskI64NativeHost.csproj
case "$profile" in
  coreclr)
    dotnet build "$project" -c Release
    compiler=(dotnet run --project "$project" -c Release --no-build --)
    ;;
  macos-arm64)
    test "$(uname -s)" = Darwin
    test "$(uname -m)" = arm64
    dotnet publish "$project" -c Release -r osx-arm64 --self-contained true -p:PublishAot=true -o "$work/compiler"
    bash verification/assert-macos-native-release.sh "$work/compiler/oresnet"
    compiler=("$work/compiler/oresnet")
    ;;
  *) exit 2 ;;
esac

positive='pub susp fnc main():int {val Future<int> f = task<int> { ^ 6 * 7; }; return await f;}'
printf '%s\n' "$positive" > "$work/canonical.atto"
printf '%s\n' "${positive/susp /suspends }" > "$work/long-marker.atto"
printf '%s\n' "${positive/6 \* 7/9223372036854775807 + 1}" > "$work/overflow.atto"
cp examples/task-i64-42.ores "$work/legacy.atto"
for case in canonical long-marker legacy overflow; do
  out="$work/$case"
  mkdir -p "$out"
  source="$work/$case.atto"
  "${compiler[@]}" check-task-i64 "$source"
  "${compiler[@]}" compile-task-i64 "$source" --out "$out/OresGenerated.dll"
  test -s "$out/OresGenerated.dll"
  if [[ "$profile" == coreclr ]]; then
    dotnet build "$host" -c Release --no-incremental -o "$out/app" -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false "-p:GeneratedAssembly=$out/OresGenerated.dll"
    runner=(dotnet "$out/app/oreslang-task-i64.dll")
  else
    dotnet publish "$host" -c Release -r osx-arm64 --self-contained true -p:PublishAot=true -o "$out/app" "-p:GeneratedAssembly=$out/OresGenerated.dll"
    bash verification/assert-macos-native-release.sh "$out/app/oreslang-task-i64"
    runner=(env -i PATH=/usr/bin:/bin "$out/app/oreslang-task-i64")
  fi
  status=0
  "${runner[@]}" > "$out/stdout" 2> "$out/stderr" || status=$?
  if [[ "$case" == overflow ]]; then
    [[ "$status" == 70 && ! -s "$out/stdout" ]]
    grep -Fx 'Task-i64 producer FAILED: OverflowException' "$out/stderr"
  else
    [[ "$status" == 0 && "$(cat "$out/stdout")" == 42 ]]
    grep -Fx 'TASK_I64_RECEIPT; producers=1; observer_distinct=True; producer_body_retired=True' "$out/stderr"
  fi
  printf 'ROOT_TASK_FORWARD_PORT %s %s status=%s source_blob=%s\n' "$profile" "$case" "$status" "$(git hash-object "$source")"
done

denials=(
  "${positive/susp /}"
  "${positive/susp /async }"
  "${positive/val Future/const Future}"
  "${positive/task<int>/sync task<int>}"
  "${positive/task<int>/task<bool>}"
  "${positive/6 \* 7/await other}"
  "${positive/6 \* 7/captured}"
  "${positive/await f/await other}"
)
for i in "${!denials[@]}"; do
  out="$work/reject-$i"; mkdir -p "$out"
  printf '%s\n' "${denials[$i]}" > "$out/invalid.atto"
  for action in check-task-i64 compile-task-i64; do
    args=("$action" "$out/invalid.atto")
    if [[ "$action" == compile-task-i64 ]]; then args+=(--out "$out/OresGenerated.dll"); fi
    status=0
    "${compiler[@]}" "${args[@]}" > "$out/stdout" 2> "$out/stderr" || status=$?
    if [[ "$status" != 1 || -e "$out/OresGenerated.dll" ]] || ! grep -Fq 'Task-i64' "$out/stderr"; then
      cat "$out/stdout" "$out/stderr" >&2
      echo "unexpected admission: $profile $i $action status=$status" >&2
      exit 1
    fi
  done
  printf 'ROOT_TASK_FORWARD_PORT_NO_ARTIFACT %s case=%s\n' "$profile" "$i"
done
for action in check compile; do
  args=("$action" "$work/canonical.atto")
  if [[ "$action" == compile ]]; then args+=(--out "$work/shipping-denied/OresGenerated.dll"); mkdir -p "$work/shipping-denied"; fi
  status=0
  "${compiler[@]}" "${args[@]}" > "$work/shipping.stdout" 2> "$work/shipping.stderr" || status=$?
  [[ "$status" == 1 && ! -e "$work/shipping-denied/OresGenerated.dll" ]]
done
echo "ROOT_TASK_FORWARD_PORT_SHIPPING_DENIED $profile"
