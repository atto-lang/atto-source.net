#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

mode=${1:-coreclr}
[[ "$mode" == coreclr || "$mode" == macos-arm64 ]]
source=tests/fixtures/task-inline-cil
compiler_project=compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
task_host=host/Ores.Net.TaskI64NativeHost/Ores.Net.TaskI64NativeHost.csproj
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
fixtures=(inline-value inline-cooperate nested-inline guard-result-task guard-nonlocal inline-writeback inline-suspends-marker inline-local-loops inline-local-loops-do inline-assignment-writeback inline-shortcircuit-writeback inline-conditional-writeback inline-match-writeback inline-ternary-writeback inline-prefix-not inline-parenthesized-not)
failures=0

# One exact compiler per lane, in a fresh private temporary directory.
# Native compiler and every linked guest must pass the same strict release
# verifier as scripts/publish-macos.sh. No managed fallback.
if [[ "$mode" == macos-arm64 ]]; then
  [[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]]
  dotnet publish "$compiler_project" -c Release -r osx-arm64 \
    --self-contained true -p:PublishAot=true -o "$work/compiler" -warnaserror
  bash verification/assert-macos-native-release.sh "$work/compiler/oresnet"
  oresnet=("$work/compiler/oresnet")
else
  dotnet build "$compiler_project" -c Release
  oresnet=(dotnet run --project "$compiler_project" -c Release --no-build --)
fi

# Compile every guest first. Report all admission failures, not just the
# first parser error after an expensive native link. These are not runtime
# receipts: the distinct SYNC_TASK_GUEST marker is emitted only after execution.
for fixture in "${fixtures[@]}" typed-spawn; do
  destination="$work/$fixture"
  mkdir -p "$destination"
  check=check
  compile=compile
  if [[ "$fixture" == typed-spawn ]]; then
    check=check-task-i64
    compile=compile-task-i64
  fi
  status=0
  "${oresnet[@]}" "$check" "$source/$fixture.atto" >"$destination/check.stdout" 2>"$destination/check.stderr" || status=$?
  if [[ "$status" != 0 ]]; then
    printf 'POSITIVE_CHECK_FAILURE %s %s status=%s\n' "$mode" "$fixture" "$status" >&2
    cat "$destination/check.stdout" "$destination/check.stderr" >&2
    failures=$((failures + 1))
  fi
  status=0
  "${oresnet[@]}" "$compile" "$source/$fixture.atto" --out "$destination/OresGenerated.dll" \
    >"$destination/compile.stdout" 2>"$destination/compile.stderr" || status=$?
  if [[ "$status" != 0 || ! -s "$destination/OresGenerated.dll" || -L "$destination/OresGenerated.dll" ]]; then
    printf 'POSITIVE_COMPILE_FAILURE %s %s status=%s\n' "$mode" "$fixture" "$status" >&2
    cat "$destination/compile.stdout" "$destination/compile.stderr" >&2
    failures=$((failures + 1))
  else
    printf 'TASK_CIL_COMPILED %s %s source_blob=%s cil_blob=%s\n' "$mode" "$fixture" \
      "$(git hash-object "$source/$fixture.atto")" "$(git hash-object "$destination/OresGenerated.dll")"
  fi
done

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
    reject-task-loop-transfer:E_SYNC_TASK_LOOP_TRANSFER_UNSUPPORTED \
    reject-long-marker-await-inline:E_SYNC_TASK_SUSPEND \
    reject-long-marker-await-outside:E_SUSPENDS_UNSUPPORTED \
    reject-long-marker-critical:E_CALLABLE_MODIFIERS \
    reject-long-marker-async:E_CALLABLE_MODIFIERS \
    reject-nested-task-loop-transfer:E_SYNC_TASK_LOOP_TRANSFER_UNSUPPORTED \
    reject-task-loop-continue:E_SYNC_TASK_LOOP_TRANSFER_UNSUPPORTED \
    reject-assignment-nested-writeback:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-assignment-shortcircuit-writeback:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-assignment-inherited-floor:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-shortcircuit-nonroot:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-shortcircuit-inherited-floor:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-conditional-nonroot:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-conditional-inherited-floor:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-conditional-nested-arm:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-match-writeback-guard:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-match-writeback-nested-arm:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-match-writeback-inherited-floor:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-match-writeback-nonroot:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-ternary-nested-arm:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-ternary-nonroot:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-ternary-inherited-floor:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    reject-ternary-predicate-writeback:E_SYNC_TASK_WRITEBACK_UNSUPPORTED \
    'reject-inline-not-int:Logical not requires bool' \
    reject-inline-macro:NET_STRUCTURED_MACRO_EXPANDER_REQUIRED \
    reject-inline-ownership-bang:OWNERSHIP_BANG_BACKEND_UNSUPPORTED \
    'reject-parenthesized-not-int:Logical not requires bool' \
    reject-spaced-macro:NET_STRUCTURED_MACRO_EXPANDER_REQUIRED \
    reject-adjacent-keyword-bang:NET_STRUCTURED_MACRO_EXPANDER_REQUIRED; do
  fixture=${case%%:*}
  expected=${case#*:}
  destination="$work/$fixture"
  mkdir -p "$destination"
  check_ok=true
  status=0
  "${oresnet[@]}" check "$source/$fixture.atto" >"$destination/check.stdout" 2>"$destination/check.stderr" || status=$?
  if [[ "$status" != 1 ]] || ! grep -Fq "$expected" "$destination/check.stderr"; then
    printf 'CHECK_DENIAL_MISMATCH %s expected=%s status=%s\n' "$fixture" "$expected" "$status" >&2
    cat "$destination/check.stdout" "$destination/check.stderr" >&2
    failures=$((failures + 1))
    check_ok=false
  fi
  status=0
  "${oresnet[@]}" compile "$source/$fixture.atto" --out "$destination/OresGenerated.dll" \
    >"$destination/compile.stdout" 2>"$destination/compile.stderr" || status=$?
  if [[ "$status" != 1 || -e "$destination/OresGenerated.dll" ]] || ! grep -Fq "$expected" "$destination/compile.stderr"; then
    printf 'COMPILE_DENIAL_MISMATCH %s expected=%s status=%s\n' "$fixture" "$expected" "$status" >&2
    cat "$destination/compile.stdout" "$destination/compile.stderr" >&2
    failures=$((failures + 1))
    continue
  fi
  if [[ "$check_ok" == true ]]; then
    printf 'SYNC_TASK_NO_ARTIFACT %s %s %s source_blob=%s\n' "$mode" "$fixture" "$expected" "$(git hash-object "$source/$fixture.atto")"
  fi
done

if [[ "$failures" != 0 ]]; then
  printf 'TASK_ADMISSION_FAILURES %s count=%s (native linking not attempted)\n' "$mode" "$failures" >&2
  exit 1
fi

# Fresh output per guest; clean host intermediates before linking each CIL
# artifact. Reuse the compiler, not a previously linked application.
for fixture in "${fixtures[@]}" typed-spawn; do
  destination="$work/$fixture"
  project="$host"
  assembly=oreslang-app
  if [[ "$fixture" == typed-spawn ]]; then
    project="$task_host"
    assembly=oreslang-task-i64
  fi
  generated="$destination/OresGenerated.dll"
  if [[ "$mode" == macos-arm64 ]]; then
    dotnet clean "$project" -c Release -r osx-arm64 "-p:GeneratedAssembly=$generated"
    dotnet publish "$project" -c Release -r osx-arm64 \
      --self-contained true -p:PublishAot=true -o "$destination/app" \
      "-p:GeneratedAssembly=$generated" -warnaserror
    binary="$destination/app/$assembly"
    bash verification/assert-macos-native-release.sh "$binary"
    runner=(env -i PATH=/usr/bin:/bin "$binary")
  else
    dotnet build "$project" -c Release --no-incremental -o "$destination/app" \
      -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false \
      "-p:GeneratedAssembly=$generated"
    runner=(dotnet "$destination/app/$assembly.dll")
  fi
  status=0
  "${runner[@]}" >"$destination/stdout" 2>"$destination/stderr" || status=$?
  if [[ "$status" != 0 || "$(cat "$destination/stdout")" != 42 ]]; then
    printf 'TASK_RUNTIME_FAILURE %s %s status=%s\n' "$mode" "$fixture" "$status" >&2
    cat "$destination/stdout" "$destination/stderr" >&2
    exit 1
  fi
  if [[ "$fixture" == typed-spawn ]]; then
    grep -Fx 'TASK_I64_RECEIPT; producers=1; observer_distinct=True; producer_body_retired=True' "$destination/stderr"
    printf 'SPAWN_TASK_GUEST %s %s value=42 source_blob=%s\n' "$mode" "$fixture" "$(git hash-object "$source/$fixture.atto")"
  else
    [[ ! -s "$destination/stderr" ]]
    printf 'SYNC_TASK_GUEST %s %s value=42 source_blob=%s\n' "$mode" "$fixture" "$(git hash-object "$source/$fixture.atto")"
  fi
done
