#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

mode=${1:-coreclr}
[[ "$mode" == coreclr || "$mode" == macos-arm64 ]]
source=tests/fixtures/task-inline-cil
compiler_project=compiler/Ores.Net.Compiler
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

if [[ "$mode" == macos-arm64 ]]; then
  dotnet restore host/Ores.Net.NativeHost -r osx-arm64
  # publish-macos verifies both Mach-O executables and uses the standalone
  # compiler to emit the input CIL before linking the app.
  for fixture in inline-value inline-cooperate nested-inline guard-result-task guard-nonlocal inline-writeback; do
    destination="artifacts/sync-task-$fixture"
    bash scripts/publish-macos.sh "$source/$fixture.atto" "$destination"
    [[ "$(env -i PATH=/usr/bin:/bin "$destination/osx-arm64/oreslang-app")" == 42 ]]
    printf 'SYNC_TASK_GUEST %s %s value=42\n' "$mode" "$fixture"
  done
  oresnet=(artifacts/compiler/osx-arm64/oresnet)
else
  dotnet build "$compiler_project" -c Release
  oresnet=(dotnet run --project "$compiler_project" -c Release --no-build --)
  for fixture in inline-value inline-cooperate nested-inline guard-result-task guard-nonlocal inline-writeback; do
    destination="$work/$fixture"
    mkdir -p "$destination"
    "${oresnet[@]}" compile "$source/$fixture.atto" --out "$destination/OresGenerated.dll"
    dotnet build host/Ores.Net.NativeHost -c Release --no-incremental -o "$destination/app" \
      -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false \
      "-p:GeneratedAssembly=$destination/OresGenerated.dll"
    [[ "$(dotnet "$destination/app/oreslang-app.dll")" == 42 ]]
    printf 'SYNC_TASK_GUEST %s %s value=42\n' "$mode" "$fixture"
  done
fi

for case in reject-inline-suspend:E_SYNC_TASK_SUSPEND reject-do-value:E_TASK_RETURN_TYPE \
    reject-bare-inline:E_SYNTAX reject-writeback:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-writeback-shortcircuit:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-writeback-nested-expression:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    'reject-writeback-immutable:Cannot reassign val binding' \
    reject-guard-inline-brace:RETURN_VALUE_REQUIRED \
    reject-guard-inline-keyword:E_GUARD \
    reject-guard-do-match:RETURN_VALUE_REQUIRED \
    reject-guard-do-task:E_GUARD \
    reject-await-after-inline:E_SUSPENDS_UNSUPPORTED \
    reject-await-nested-inline:E_SYNC_TASK_SUSPEND \
    reject-try-inline:E_LOCAL_TRY_RETURN_UNSUPPORTED \
    reject-try-match:E_LOCAL_TRY_RETURN_UNSUPPORTED \
    reject-defer-inline:E_LEXICAL_CLEANUP_UNSUPPORTED \
    reject-guard-cleanup:E_LEXICAL_CLEANUP_UNSUPPORTED \
    reject-using-inline:E_LEXICAL_CLEANUP_UNSUPPORTED \
    reject-task-loop-transfer:E_SYNC_TASK_LOOP_TRANSFER_UNSUPPORTED; do
  fixture=${case%%:*}
  expected=${case#*:}
  destination="$work/$fixture"
  mkdir -p "$destination"
  status=0
  "${oresnet[@]}" check "$source/$fixture.atto" >"$destination/check.stdout" 2>"$destination/check.stderr" || status=$?
  if [[ "$status" != 1 ]] || ! grep -Fq "$expected" "$destination/check.stderr"; then
    printf 'CHECK_DENIAL_MISMATCH %s expected=%s status=%s\n' "$fixture" "$expected" "$status" >&2
    cat "$destination/check.stdout" "$destination/check.stderr" >&2
    exit 1
  fi
  status=0
  "${oresnet[@]}" compile "$source/$fixture.atto" --out "$destination/OresGenerated.dll" \
    >"$destination/compile.stdout" 2>"$destination/compile.stderr" || status=$?
  if [[ "$status" != 1 || -e "$destination/OresGenerated.dll" ]] || ! grep -Fq "$expected" "$destination/compile.stderr"; then
    printf 'COMPILE_DENIAL_MISMATCH %s expected=%s status=%s\n' "$fixture" "$expected" "$status" >&2
    cat "$destination/compile.stdout" "$destination/compile.stderr" >&2
    exit 1
  fi
  printf 'SYNC_TASK_NO_ARTIFACT %s %s %s\n' "$mode" "$fixture" "$expected"
done

destination="$work/typed-spawn"
mkdir -p "$destination"
"${oresnet[@]}" compile-task-i64 "$source/typed-spawn.atto" --out "$destination/OresGenerated.dll"
host=host/Ores.Net.TaskI64NativeHost
if [[ "$mode" == macos-arm64 ]]; then
  dotnet publish "$host" -c Release -r osx-arm64 -o "$destination/app" \
    "-p:GeneratedAssembly=$destination/OresGenerated.dll"
  binary="$destination/app/oreslang-task-i64"
  file "$binary" | grep -E 'Mach-O.*arm64'
  runner=(env -i PATH=/usr/bin:/bin "$binary")
else
  dotnet build "$host" -c Release --no-incremental -o "$destination/app" \
    -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false \
    "-p:GeneratedAssembly=$destination/OresGenerated.dll"
  runner=(dotnet "$destination/app/oreslang-task-i64.dll")
fi
[[ "$("${runner[@]}" 2>"$destination/receipt")" == 42 ]]
grep -Fx 'TASK_I64_RECEIPT; producers=1; observer_distinct=True; producer_body_retired=True' "$destination/receipt"
printf 'SPAWN_TASK_GUEST %s typed-spawn value=42\n' "$mode"
