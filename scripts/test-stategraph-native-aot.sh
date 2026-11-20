#!/usr/bin/env bash
# Native AOT compiler and application proof, never managed fallback.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(git rev-parse HEAD)" = "${EXPECTED_SOURCE_SHA:?exact head required}" ]]
for name in scalar structured reject-generic; do
  source="examples/stategraph-portability/$name.ores"
  [[ -s "$source" && ! -L "$source" ]]
  git ls-files --error-unmatch -- "$source" >/dev/null
done
shared=contracts/stategraph-portability/shared-scalar.ores
[[ -f "$shared" && ! -L "$shared" ]]
git ls-files --error-unmatch -- "$shared" >/dev/null
[[ "$(git hash-object "$shared")" = "ec1cb5e6cc38c26842d724ccacb0df028b702c3f" ]] || {
  echo 'SG_NET_COMMON_SCALAR_SOURCE_MISMATCH' >&2; exit 1;
}
bash scripts/publish-linux.sh "$shared" artifacts/stategraph-scalar
compiler="artifacts/compiler/linux-x64/oresnet"
scalar="artifacts/stategraph-scalar/linux-x64/oreslang-app"
bash verification/assert-linux-native-release.sh "$compiler"
bash verification/assert-linux-native-release.sh "$scalar"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
printf '42\n' > "$tmp/expected"
run_aot_guest() {
  local binary="$1" label="$2" status
  : > "$tmp/out"
  : > "$tmp/err"
  set +e
  timeout --kill-after=3s 45s "$binary" > "$tmp/out" 2> "$tmp/err"
  status=$?
  set -e
  if [[ "$status" -ne 0 ]] || ! cmp -s "$tmp/out" "$tmp/expected" || [[ -s "$tmp/err" ]]; then
    cat "$tmp/err" >&2
    echo "SG_NET_NATIVE_GUEST_NOT_ACCEPTED label=$label status=$status" >&2
    exit 1
  fi
}
run_aot_guest "$scalar" shared-scalar
"$compiler" check "$shared"
"$compiler" check examples/stategraph-portability/scalar.ores
printf 'SG_NET_SHARED_SCALAR_NATIVE_PASS blob=%s guest_stdout=42\n' "$(git rev-parse "HEAD:$shared")"
# Only the experimental structured lane may accept graph-step control flow.
if "$compiler" check examples/stategraph-portability/structured.ores; then
  echo 'SG_NET_UNSAFE: structured source silently admitted to scalar lane' >&2
  exit 1
fi
bash scripts/publish-linux.sh examples/stategraph-portability/structured.ores artifacts/stategraph-structured --structured
structured="artifacts/stategraph-structured/linux-x64/oreslang-app"
bash verification/assert-linux-native-release.sh "$structured"
run_aot_guest "$structured" structured
echo "SG_NET_NATIVE_STRUCTURED_PASS source=$(git rev-parse HEAD) blob=$(git rev-parse HEAD:examples/stategraph-portability/structured.ores)"
denied="artifacts/rejected-stategraph/linux-x64/OresGenerated.dll"
mkdir -p "$(dirname "$denied")"
rm -f "$denied"
set +e
timeout --kill-after=3s 45s "$compiler" compile-structured \
  examples/stategraph-portability/reject-generic.ores --out "$denied" \
  > "$tmp/negative.out" 2> "$tmp/negative.err"
negative_status=$?
set -e
if [[ "$negative_status" -ne 1 ]] || [[ -s "$tmp/negative.out" ]] ||
   [[ ! -s "$tmp/negative.err" ]]; then
  echo "SG_NET_UNSAFE: negative compiler check did not return diagnosed rejection status=$negative_status" >&2
  exit 1
fi
[[ ! -e "$denied" ]] || { echo 'SG_NET_UNSAFE: denied source emitted assembly' >&2; exit 1; }
# This is the exact canonical userland GraphState<T> source shared with all
# five backends, not a backend-specific malformed lookalike.
generic=contracts/stategraph-portability/shared-generic-state.ores
[[ -f "$generic" && ! -L "$generic" ]] ||
  { echo 'SG_NET_GENERIC_SOURCE_MISSING' >&2; exit 1; }
git ls-files --error-unmatch -- "$generic" >/dev/null
[[ "$(git hash-object "$generic")" = "c3a67b837a73f427a6157c446cc6e3989e9a66da" ]] ||
  { echo 'SG_NET_GENERIC_SOURCE_DRIFT' >&2; exit 1; }
[[ "$(git rev-parse "HEAD:$generic")" = "c3a67b837a73f427a6157c446cc6e3989e9a66da" ]]
set +e
timeout --kill-after=3s 45s "$compiler" check-structured "$generic" > "$tmp/shared.generic.out" 2> "$tmp/shared.generic.err"
shared_generic_status=$?
set -e
if [[ "$shared_generic_status" -ne 1 || -s "$tmp/shared.generic.out" || ! -s "$tmp/shared.generic.err" ]]; then
  echo "SG_NET_GENERIC_NOT_REJECTED_BY_COMPILER status=$shared_generic_status" >&2
  exit 1
fi
echo 'SG_NET_CANONICAL_GENERIC_UNSUPPORTED (NOT parity achieved)'
echo 'SG_NET_NATIVE_GENERIC_REJECTED (NOT full StateGraph parity)'
