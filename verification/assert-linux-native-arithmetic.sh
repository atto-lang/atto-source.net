#!/usr/bin/env bash
# Real Linux Native AOT arithmetic negatives: do not confuse source rejection
# with a generated program's checked-runtime trap.
set -Eeuo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
# Pin the exact negative and checked-overflow inputs. Otherwise a changed
# fixture could satisfy the diagnostic/exit checks without exercising the
# signed arithmetic boundaries this parity gate claims to prove.
for fixture in division-by-zero division-overflow overflow; do
  case "$fixture" in
    division-by-zero) expected_blob=75b50969cad9f954aa103b530c095832d3b1b97d ;;
    division-overflow) expected_blob=ce0bed589b5981c0855d9d88237245261aa28a2b ;;
    overflow) expected_blob=b5ebfd6d354182ce9f9da568ebb2ab787db9366e ;;
  esac
  actual_blob="$(git hash-object -- "examples/$fixture.ores")"
  if [[ "$actual_blob" != "$expected_blob" ]]; then
    printf 'Arithmetic fixture drift: %s has blob %s, expected %s\n' \
      "$fixture" "$actual_blob" "$expected_blob" >&2
    exit 1
  fi
done
compiler=artifacts/compiler/linux-x64/oresnet
[[ -f "$compiler" && -x "$compiler" && ! -L "$compiler" ]] || {
  echo 'Native AOT compiler is missing or unsafe' >&2
  exit 1
}
# Keep diagnostics, including failed probes, in the uploaded evidence bundle.
evidence="$root/artifacts/evidence"
[[ "$(realpath -m -- "$evidence")" = "$evidence" ]] || exit 1
mkdir -p -- "$evidence"
rm -f -- "$evidence/linux-native-arithmetic.json"
work="$(mktemp -d "$evidence/arithmetic.XXXXXXXX")"

for name in division-by-zero division-overflow; do
  source_file="examples/$name.ores"
  case_dir="$work/$name"
  mkdir -p -- "$case_dir"
  # oresnet only accepts a generated assembly named OresGenerated.dll.
  # Give each rejection a unique directory so the compiler can reach the
  # source parser rather than rejecting our test's --out basename first.
  denied="$case_dir/OresGenerated.dll"
  diagnostic="$case_dir/diagnostic.log"
  if timeout --signal=TERM --kill-after=5s 30s "$compiler" compile "$source_file" --out "$denied" >"$diagnostic" 2>&1; then
    echo "Source division unexpectedly compiled: $source_file" >&2
    exit 1
  else
    status=$?
  fi
  printf '%s\n' "$status" > "$case_dir/exit-code.txt"
  cat -- "$diagnostic"
  if [[ "$status" -ne 1 ]]; then
    printf 'Unexpected native compiler failure for %s: exit %s, expected source rejection 1\n' \
      "$source_file" "$status" >&2
    cat -- "$diagnostic" >&2
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
  printf 'SOURCE_REJECTION fixture=%s exit=%s emitted_cil=false\n' "$name" "$status"
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
printf '%s\n' "$output" > "$work/overflow.log"
printf '%s\n' "$status" > "$work/overflow-exit-code.txt"
printf 'CHECKED_OVERFLOW exit=%s diagnostic=%s\n' "$status" "$output"
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
  actual="$(timeout --signal=TERM --kill-after=5s 30s "$binary")"
  if [[ "$actual" != "$expected" ]]; then
    printf 'Native AOT source isolation failed: %s returned %q, expected %q\n' \
      "$binary" "$actual" "$expected" >&2
    exit 1
  fi
  printf 'NATIVE_SOURCE_ISOLATION binary=%s output=%s\n' "$binary" "$actual"
done
printf '{"schema":"oreslang.net.linux_native_arithmetic_v1","division_rejection_exit":1,"division_emitted_cil":false,"overflow_exit":%s,"outputs":[14,42,40,45],"native_application_count":5}\n' \
  "$status" > "$evidence/linux-native-arithmetic.json"
echo 'LINUX_NET_NATIVE_AOT_CHECKED_OVERFLOW_EXIT70_AND_FIVE_SOURCE_ISOLATION'
