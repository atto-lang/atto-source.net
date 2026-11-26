#!/usr/bin/env bash
# Closed root TaskFuture/Future NativeAOT guest gate.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${EXPECTED_HEAD:?pin exact source head}"
[[ "$EXPECTED_HEAD" =~ ^[a-f0-9]{40}$ && "$(git rev-parse HEAD)" == "$EXPECTED_HEAD" ]]
[[ -z "$(git status --porcelain --untracked-files=no)" ]]
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]]
root="$(pwd -P)"
fixture_dir="$root/tests/fixtures/portable-task-v2-pilot"
[[ ! -L "$fixture_dir/taskfuture-int.atto" && "$(git hash-object --no-filters "$fixture_dir/taskfuture-int.atto")" == "32aa66ebc3ee9418d1da088393bb66762a67a881" ]]
[[ ! -L "$fixture_dir/future-int.atto" && "$(git hash-object --no-filters "$fixture_dir/future-int.atto")" == "63d69d589614250aa191510fee2990e7f3499aef" ]]
[[ ! -L "$fixture_dir/taskfuture-string.atto" && "$(git hash-object --no-filters "$fixture_dir/taskfuture-string.atto")" == "29eea1fc75ae3bd79a7156a06efa3318a94a4c63" ]]
[[ ! -L "$fixture_dir/reject-double-await.atto" && "$(git hash-object --no-filters "$fixture_dir/reject-double-await.atto")" == "bb866b2baf700a85e1c105b623c485aaa4189bbe" ]]
[[ ! -L "$fixture_dir/reject-const-taskfuture.atto" && "$(git hash-object --no-filters "$fixture_dir/reject-const-taskfuture.atto")" == "34eb2855945e9196f522fdb7e34ad609cd971d43" ]]
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
# Require successful process termination, not merely expected stdout before a failure.
run_guest() {
  local app="$1" out="$2" status=0
  timeout 15s "$app" >"$out/guest.out" 2>"$out/guest.err" || status=$?
  [[ "$status" == 0 && "$(cat "$out/guest.out")" == 42 ]] || return 1
  grep -Fx 'TASK_I64_RECEIPT; producers=1; observer_distinct=True; producer_body_retired=True' "$out/guest.err"
}
# Synthetic gate regression only; never counts as a compiled guest.
mkdir "$work/gate-control"
cat >"$work/gate-control/bad" <<'CONTROL'
#!/usr/bin/env bash
printf '42\n'
printf '%s\n' 'TASK_I64_RECEIPT; producers=1; observer_distinct=True; producer_body_retired=True' >&2
exit 7
CONTROL
chmod +x "$work/gate-control/bad"
if run_guest "$work/gate-control/bad" "$work/gate-control"; then
  echo "guest gate accepted nonzero exit" >&2
  exit 1
fi
sed 's/exit 7/exit 0/' "$work/gate-control/bad" >"$work/gate-control/good"
chmod +x "$work/gate-control/good"
run_guest "$work/gate-control/good" "$work/gate-control" >/dev/null
sed 's/observer_distinct=True/observer_distinct=False/' "$work/gate-control/good" >"$work/gate-control/bad-receipt"
chmod +x "$work/gate-control/bad-receipt"
if run_guest "$work/gate-control/bad-receipt" "$work/gate-control"; then
  echo "guest gate accepted incorrect physical receipt" >&2
  exit 1
fi
echo CLOSED_TASK_NATIVEAOT_GATE_CONTROL_PASS
project=compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj
host=host/Ores.Net.TaskI64NativeHost/Ores.Net.TaskI64NativeHost.csproj
dotnet publish "$project" -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o "$work/compiler" -warnaserror
compiler="$work/compiler/oresnet"
bash verification/assert-linux-native-release.sh "$compiler"
compiler_sha="$(sha256sum "$compiler" | cut -d' ' -f1)"
for name in taskfuture-int future-int; do
  out="$work/$name"; mkdir "$out"
  source="$fixture_dir/$name.atto"
  fixture_sha="$(sha256sum "$source" | cut -d' ' -f1)"
  "$compiler" check-task-i64 "$source"
  "$compiler" compile-task-i64 "$source" --out "$out/OresGenerated.dll"
  [[ -s "$out/OresGenerated.dll" && ! -L "$out/OresGenerated.dll" ]]
  dotnet clean "$host" -c Release -r linux-x64 "-p:GeneratedAssembly=$out/OresGenerated.dll"
  dotnet publish "$host" -c Release -r linux-x64 --self-contained true -p:PublishAot=true "-p:GeneratedAssembly=$out/OresGenerated.dll" -o "$out/app" -warnaserror
  app="$out/app/oreslang-task-i64"
  bash verification/assert-linux-native-release.sh "$app"
  run_guest "$app" "$out"
  [[ "$(sha256sum "$source" | cut -d' ' -f1)" == "$fixture_sha" ]]
  [[ "$(sha256sum "$compiler" | cut -d' ' -f1)" == "$compiler_sha" ]]
  printf 'CLOSED_TASK_NATIVEAOT_PASS|head=%s|case=%s|value=42|compiler_sha256=%s|fixture_sha256=%s|cil_sha256=%s|artifact_sha256=%s\n' "$EXPECTED_HEAD" "$name" "$compiler_sha" "$fixture_sha" "$(sha256sum "$out/OresGenerated.dll" | cut -d' ' -f1)" "$(sha256sum "$app" | cut -d' ' -f1)"
done
for name in reject-double-await reject-const-taskfuture taskfuture-string; do
  out="$work/$name"; mkdir "$out"
  source="$fixture_dir/$name.atto"
  for action in check-task-i64 compile-task-i64; do
    args=("$action" "$source")
    if [[ "$action" == compile-task-i64 ]]; then args+=(--out "$out/OresGenerated.dll"); fi
    status=0
    timeout 30s "$compiler" "${args[@]}" >"$out/compile.log" 2>&1 || status=$?
    [[ "$status" == 1 && ! -e "$out/OresGenerated.dll" && ! -L "$out/OresGenerated.dll" ]]
    case "$name" in
      reject-double-await) grep -Fq E_TASK_RESULT_CONSUMED "$out/compile.log";;
      reject-const-taskfuture) grep -Fq E_CONST "$out/compile.log";;
      taskfuture-string) grep -Fq 'Unsupported Task-i64 source character' "$out/compile.log";;
    esac
  done
  printf 'CLOSED_TASK_NATIVEAOT_REJECT|head=%s|case=%s|no_artifact=yes\n' "$EXPECTED_HEAD" "$name"
done
[[ "$(sha256sum "$compiler" | cut -d' ' -f1)" == "$compiler_sha" ]]
[[ -z "$(git status --porcelain --untracked-files=no)" ]]
