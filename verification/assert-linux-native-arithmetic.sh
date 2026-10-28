#!/usr/bin/env bash
# Real Linux Native AOT arithmetic negatives: do not confuse source rejection
# with a generated program's checked-runtime trap.
set -Eeuo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
compiler=artifacts/compiler/linux-x64/oresnet
[[ -f "$compiler" && -x "$compiler" && ! -L "$compiler" ]] || {
  echo 'Native AOT compiler is missing or unsafe' >&2
  exit 1
}
work="$(mktemp -d "${TMPDIR:-/tmp}/ores-net-native-arithmetic.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT

for name in division-by-zero division-overflow; do
  source_file="examples/$name.ores"
  denied="$work/$name.dll"
  diagnostic="$work/$name.log"
  if "$compiler" compile "$source_file" --out "$denied" >"$diagnostic" 2>&1; then
    echo "Source division unexpectedly compiled: $source_file" >&2
    exit 1
  fi
  if ! grep -Fqi 'float result lowering' "$diagnostic"; then
    echo "Missing float-lowering denial for $source_file" >&2
    cat -- "$diagnostic" >&2
    exit 1
  fi
  if [[ -e "$denied" || -L "$denied" ]]; then
    echo "Rejected source emitted a CIL artifact: $source_file" >&2
    exit 1
  fi
done
echo 'LINUX_NET_NATIVE_AOT_SOURCE_DIVISION_DENIED_WITHOUT_CIL'

# The overflow example IS valid checked-i64 source. It must compile and then
# trap in the separately published Native AOT executable, matching Windows.
bash scripts/publish-linux.sh examples/overflow.ores artifacts/negative-overflow
app=artifacts/negative-overflow/linux-x64/oreslang-app
bash verification/assert-linux-native-release.sh "$app"
set +e
output="$(timeout --signal=TERM --kill-after=5s 30s "$app" 2>&1)"
status=$?
set -e
if [[ "$status" -ne 70 ]] || ! grep -Fqi 'overflow' <<<"$output"; then
  printf 'Linux checked overflow: expected exit 70 and overflow diagnostic, got exit %s and output %q\n' \
    "$status" "$output" >&2
  exit 1
fi

# A fifth generated source must not contaminate any previously linked image.
for pair in \
  'artifacts/apps/linux-x64/oreslang-app:14' \
  'artifacts/parity/linux-x64/oreslang-app:42' \
  'artifacts/parity-precedence/linux-x64/oreslang-app:40' \
  'artifacts/structured/linux-x64/oreslang-app:45'; do
  binary="${pair%%:*}"
  expected="${pair##*:}"
  actual="$("$binary")"
  if [[ "$actual" != "$expected" ]]; then
    printf 'Native AOT source isolation failed: %s returned %q, expected %q\n' \
      "$binary" "$actual" "$expected" >&2
    exit 1
  fi
done
echo 'LINUX_NET_NATIVE_AOT_CHECKED_OVERFLOW_EXIT70_AND_FIVE_SOURCE_ISOLATION'
