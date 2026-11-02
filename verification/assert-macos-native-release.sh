#!/usr/bin/env bash
# Header/layout verifier. NativeAOT proof additionally requires a stepful
# dotnet publish of generated guest CIL and execution of this binary.
set -Eeuo pipefail
if [[ $# -ne 1 ]]; then
  echo 'usage: bash verification/assert-macos-native-release.sh binary' >&2
  exit 2
fi
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || {
  echo 'macOS ARM64 native verification requires an Apple Silicon host' >&2
  exit 2
}
binary="$1"
[[ -f "$binary" && -x "$binary" && ! -L "$binary" ]] || {
  echo "Missing, non-executable, or symlinked native binary: $binary" >&2
  exit 1
}
file -b "$binary" | grep -Eq '^Mach-O 64-bit .*arm64.*executable|^Mach-O 64-bit .*executable.*arm64' || {
  echo "Expected a Mach-O arm64 executable: $binary" >&2
  exit 1
}
[[ "$(lipo -archs "$binary")" == arm64 ]] || {
  echo "Native image must have exactly the arm64 architecture: $binary" >&2
  exit 1
}
directory="$(dirname "$binary")"
for candidate in "$directory"/*.dll "$directory"/*.deps.json "$directory"/*.runtimeconfig.json; do
  if [[ -e "$candidate" || -L "$candidate" ]]; then
    echo "Managed runtime sidecar forbidden in strict macOS AOT output: $candidate" >&2
    exit 1
  fi
done
echo "Verified standalone Mach-O ARM64 layout: $binary"
