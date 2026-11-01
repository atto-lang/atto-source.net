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
[[ "$("$scalar")" = "42" ]] || { echo 'SG_NET_SCALAR_GUEST_WRONG_RESULT' >&2; exit 1; }
"$compiler" check examples/stategraph-portability/scalar.ores
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
[[ "$("$structured")" = "42" ]] || { echo 'SG_NET_STRUCTURED_GUEST_WRONG_RESULT' >&2; exit 1; }
echo "SG_NET_NATIVE_STRUCTURED_PASS source=$(git rev-parse HEAD) blob=$(git rev-parse HEAD:examples/stategraph-portability/structured.ores)"
denied="artifacts/rejected-stategraph/linux-x64/OresGenerated.dll"
mkdir -p "$(dirname "$denied")"
rm -f "$denied"
if "$compiler" compile-structured examples/stategraph-portability/reject-generic.ores --out "$denied"; then
  echo 'SG_NET_UNSAFE: unsupported generic graph code admitted' >&2
  exit 1
fi
[[ ! -e "$denied" ]] || { echo 'SG_NET_UNSAFE: denied source emitted assembly' >&2; exit 1; }
echo 'SG_NET_NATIVE_GENERIC_REJECTED (NOT full StateGraph parity)'
