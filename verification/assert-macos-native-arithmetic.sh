#!/usr/bin/env bash
# AOT guest arithmetic evidence; only passes after a real Apple Silicon host
# has compiled and executed a distinct generated-CIL NativeAOT executable.
set -Eeuo pipefail
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || {
  echo 'Native negative matrix requires a macOS ARM64 runner' >&2
  exit 2
}
root="$(cd "$(dirname "$0")/.." && pwd -P)"
cd "$root"
compiler=artifacts/compiler/osx-arm64/oresnet
[[ -x "$compiler" && ! -L "$compiler" ]] || {
  echo 'Publish the real NativeAOT compiler before arithmetic negatives' >&2
  exit 2
}
work="$(mktemp -d "${TMPDIR:-/tmp}/atto-macos-aot-negative.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT
for name in division-by-zero division-overflow; do
  denied="$work/$name/OresGenerated.dll"
  mkdir -p "$(dirname "$denied")"
  if diagnostic="$("$compiler" compile "examples/$name.ores" --out "$denied" 2>&1)"; then
    echo "$name improperly admitted into CIL emission: $diagnostic" >&2
    exit 1
  else
    code=$?
  fi
  [[ $code -eq 1 && "$diagnostic" == *"float result lowering"* && ! -e "$denied" && ! -L "$denied" ]] || {
    echo "$name did not fail closed: code=$code diagnostic=$diagnostic" >&2
    exit 1
  }
  echo "MACOS_NET_NATIVE_AOT_SOURCE_REJECTED_$name"
done

# Constant signed-i64 overflow is accepted as source/CIL and must trap at
# runtime. Isolate its native app so hello/add binaries remain untouched.
bash scripts/publish-macos.sh examples/overflow.ores artifacts/native-negative-overflow
binary=artifacts/native-negative-overflow/osx-arm64/oreslang-app
bash verification/assert-macos-native-release.sh "$binary"
if diagnostic="$(env -i PATH=/usr/bin:/bin "$binary" 2>&1)"; then
  echo "Signed i64 overflow unexpectedly succeeded: $diagnostic" >&2
  exit 1
else
  code=$?
fi
[[ $code -eq 70 && "$diagnostic" == *overflow* ]] || {
  echo "Expected NativeAOT overflow trap exit 70; code=$code diagnostic=$diagnostic" >&2
  exit 1
}
echo 'MACOS_NET_NATIVE_AOT_OVERFLOW_GUEST_EXIT70_EXECUTED'
