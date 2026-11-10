#!/usr/bin/env bash
# Actual native x64 .NET AOT executable verification, NOT a CIL reference oracle.
# Explicit opt-in: eight independent NativeAOT app builds; requires .NET SDK 10.
# Source text/hash must match conformance PR #275 exactly (LF, UTF-8 no BOM).
set -Eeuo pipefail
umask 077
root="$(cd -- "$(dirname -- "$0")/.." && pwd -P)"
cd "$root"
for tool in dotnet sha256sum cmp mktemp file readelf; do
  command -v "$tool" >/dev/null || { echo "missing prerequisite: $tool" >&2; exit 2; }
done
work="$(mktemp -d "${TMPDIR:-/tmp}/oresnet-eight-XXXXXX")"
trap 'rm -rf -- "$work"' EXIT
verify_case() {
  local id="$1" source_text="$2" expected_digest="$3" kind="$4" expected_stdout="$5"
  local source="$work/input.ores" app="artifacts/oresllm_native_scalar/linux-x64/oreslang-app"
  printf '%s\n' "$source_text" > "$source"
  printf '%s  %s\n' "$expected_digest" "$source" | sha256sum --status -c - || {
    echo "DENIED: wrong exact .ores bytes for $id" >&2
    exit 1
  }
  bash scripts/publish-linux.sh "$source" artifacts/oresllm_native_scalar >"$work/publish.log" 2>"$work/publish.err" || {
    cat "$work/publish.err" >&2
    echo "DENIED: compiler/NativeAOT publication failed for $id" >&2
    exit 1
  }
  bash verification/assert-linux-native-release.sh "$app" >/dev/null
  local status=0
  "$app" > "$work/stdout" 2> "$work/stderr" || status=$?
  if [[ "$kind" == accept ]]; then
    [[ "$status" -eq 0 && ! -s "$work/stderr" ]] || {
      echo "DENIED: nonzero exit/stderr on $id (exit=$status)" >&2; exit 1;
    }
    printf '%s\n' "$expected_stdout" > "$work/expected"
    cmp --silent "$work/stdout" "$work/expected" || {
      echo "DENIED: incorrect guest stdout for $id" >&2; exit 1;
    }
  else
    [[ "$kind" == overflow && "$status" -eq 70 && ! -s "$work/stdout" ]] || {
      echo "DENIED: checked-i64 overflow status/stdout mismatch on $id" >&2; exit 1;
    }
    printf 'Oreslang arithmetic overflow\n' > "$work/expected"
    cmp --silent "$work/stderr" "$work/expected" || {
      echo "DENIED: overflow misclassified or native guest crashed for $id" >&2; exit 1;
    }
  fi
  printf 'NATIVE_AOT_CASE_OK|%s|%s|%s\n' "$id" "$expected_digest" "$status"
}
verify_case "literal-42" "pub fnc main(): int { return 42; }" "a471a693741ed13b31ee1a38dc362c1a4140162a635ccd815f61dec318e990b4" "accept" "42"
verify_case "precedence" "pub fnc main(): int { return 4 + 6 * (5 - 1); }" "aa294609eb38ba63571ebc09da5fa8a7a108ec9a8c4e7156c609e7101da0615b" "accept" "28"
verify_case "unary" "pub fnc main(): int { return -(7 * 3); }" "8ac156b0468e6f8e61b25d6bb59cd2d608b57cb2c4d077776f5a0483458b138a" "accept" "-21"
verify_case "i64-min" "pub fnc main(): int { return -9223372036854775808; }" "8b5b69ec25fda12bd6a695c7a2ab986634bcbf37f8b129507582597e662c0b49" "accept" "-9223372036854775808"
verify_case "i64-max" "pub fnc main(): int { return 9223372036854775807; }" "80e89020eae9f56c657043d6482bd71f3e2c9aa16730438c102e9ab5834995a4" "accept" "9223372036854775807"
verify_case "max-minus-one" "pub fnc main(): int { return 9223372036854775807 - 1; }" "d27086270301951421718bcf3c82d5fe988b5dd076d43153f3aed3a6bebb7c99" "accept" "9223372036854775806"
verify_case "overflow-add" "pub fnc main(): int { return 9223372036854775807 + 1; }" "f1707d7b751858fe61bac9c13e68deca63f6dfa12305a9e51ad44d214b78b74d" "overflow" ""
verify_case "overflow-multiply" "pub fnc main(): int { return 4611686018427387904 * 2; }" "a391373cd6e003e6f3ca7db697fe11116c8561aba86c68e4803922a0b933eff0" "overflow" ""
printf 'NATIVE_AOT_EIGHT_SCALAR_CASES_OK|cases=8\n'
