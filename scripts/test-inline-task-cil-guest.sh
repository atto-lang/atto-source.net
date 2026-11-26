#!/usr/bin/env bash
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts
work="$(mktemp -d)"
trap 'rm -rf "$work"; rm -f "$guest"' EXIT
dotnet build "$compiler" -c Release
run42() {
  local rc
  set +e
  "$@" >"$work/stdout" 2>"$work/stderr"
  rc=$?
  set -e
  test "$rc" -eq 42 && test ! -s "$work/stdout" && test ! -s "$work/stderr" || { cat "$work/stdout" "$work/stderr"; echo "TASK_EXPECT_42_FAILED $rc"; exit 1; }
}
for name in sync-task-empty sync-task-inline-value sync-task-nested-discard sync-task-function-call; do
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "fixtures/cil/$name.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  run42 dotnet run --no-build -c Release --project "$host"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$work/native-$name"
  run42 "$work/native-$name/Atto.Cil.Guest.Host"
  echo "INLINE_TASK_EXECUTED $name CoreCLR+NativeAOT"
done
reject() {
  local src="$1" code="$2" rc
  shift 2
  rm -f "$work/reject.dll"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$@" "$src" "$work/reject.dll" >"$work/stdout" 2>"$work/stderr"
  rc=$?
  set -e
  test "$rc" -eq 2 && grep -Fq "$code:" "$work/stderr" && test ! -e "$work/reject.dll" || { cat "$work/stderr"; echo "TASK_REJECT_FAILED $src $code $rc"; exit 1; }
  echo "INLINE_TASK_REJECTED $src $code"
}
for entry in taskfuture-typed-constructor-unsupported:E_TASK_UNSUPPORTED taskfuture-get-ready-unsupported:E_TASK_UNSUPPORTED taskfuture-cancel-task-unsupported:E_TASK_UNSUPPORTED taskfuture-cooperate-unsupported:E_TASK_UNSUPPORTED taskfuture-receipt-split-unsupported:E_TASK_UNSUPPORTED typed-task-unsupported:E_TASK_UNSUPPORTED do-task-unsupported:E_TASK_UNSUPPORTED do-sync-task-nonvoid:E_TASK_RETURN_TYPE do-sync-task-inferred-nonvoid:E_TASK_RETURN_TYPE do-sync-task-nested-nonvoid:E_TASK_RETURN_TYPE sync-task-standalone-void:E_SYNTAX sync-task-standalone-typed-void:E_SYNTAX sync-task-nested-standalone-void:E_SYNTAX sync-task-standalone-nonvoid:E_SYNTAX sync-task-missing-return:E_TASK_RETURN_TYPE sync-task-void-as-value:E_TASK_RETURN_TYPE sync-task-bad-type:E_TASK_UNSUPPORTED sync-task-await-unsupported:E_TASK_UNSUPPORTED sync-task-void-return-type:E_TASK_RETURN_TYPE sync-task-double-return:E_PARSE sync-reserved:E_RESERVED; do
  reject "fixtures/cil/negative/${entry%%:*}.atto" "${entry#*:}"
done
printf 'fnc output(): int { stdio.println(1); return 42; } fnc bridge(): int { return output(); } pub fnc main(): int { return sync task<int> { return bridge(); }; }\n' >"$work/effect.atto"
reject "$work/effect.atto" E_TASK_EFFECT --allow-stdio
printf 'pub fnc main(): int { return sync task<int> { return 1 > 0; }; }\n' >"$work/bool.atto"
reject "$work/bool.atto" E_TASK_RETURN_TYPE
echo ATTO_INLINE_TASK_CIL_INTEGRATION_PASS
