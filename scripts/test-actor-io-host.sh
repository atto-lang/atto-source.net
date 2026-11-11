#!/usr/bin/env bash
# Execute source-owned .NET actor mailbox/I/O host tests at an exact Git head.
# This does NOT prove .atto guest execution or NativeAOT.
set -euo pipefail
cd "$(dirname "$0")/.."

command -v git >/dev/null
command -v dotnet >/dev/null || {
  echo "BLOCKED_NO_DOTNET_SDK: managed actor host tests not executed" >&2
  exit 127
}

actual_head="$(git rev-parse HEAD)"
actual_tree="$(git rev-parse 'HEAD^{tree}')"
if [ -n "${EXPECTED_HEAD:-}" ] && [ "$actual_head" != "$EXPECTED_HEAD" ]; then
  echo "FAILED_SOURCE_HEAD: expected=$EXPECTED_HEAD actual=$actual_head" >&2
  exit 1
fi
if [ -n "$(git status --porcelain)" ]; then
  echo "FAILED_DIRTY_SOURCE: refusing non-reproducible actor test evidence" >&2
  exit 1
fi

printf 'ACTOR_IO_HOST_SOURCE_HEAD=%s\n' "$actual_head"
printf 'ACTOR_IO_HOST_SOURCE_TREE=%s\n' "$actual_tree"
dotnet --info
project="verification/Ores.Net.Tests/Ores.Net.Tests.csproj"
# Required strict compile and *executed* managed test runner. Failure in
# either command is never reported as a successful test receipt.
dotnet build "$project" -c Release -warnaserror
log="${ACTOR_MANAGED_LOG:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/actor-managed-tests.log}"
mkdir -p "$(dirname "$log")"
dotnet run --project "$project" -c Release --no-build | tee "$log"
for receipt in \
  'PASS actor scalar frame ABI v1 host/compiler-boundary tests (NOT guest execution)' \
  ACTOR_IO_ENVELOPE_RETIREMENT_PASS \
  ACTOR_IO_HALF_CLOSE_PASS \
  ACTOR_IO_HOST_INTEGRATION_PASS \
  ACTOR_IO_GLOBAL_BUDGET_PASS \
  ACTOR_IO_READY_SCHEDULER_PASS \
  ACTOR_SUPERVISOR_CONTROL_RECEIPT_PASS \
  ACTOR_PIPELINE_MAILBOX_CONTROL_PASS
do
  grep -Fx -- "$receipt" "$log"
done
echo "ACTOR_IO_HOST_EXACT_HEAD_TESTS_EXECUTED_PASS"
