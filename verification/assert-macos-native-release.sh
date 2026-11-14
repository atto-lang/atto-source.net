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
# Case-insensitive names must fail even on case-sensitive macOS volumes.
# NativeAOT release output must not ship a managed host runtime or sidecars.
shopt -s nullglob nocaseglob
for candidate in "$directory"/*.dll "$directory"/*.deps.json "$directory"/*.runtimeconfig.json \
    "$directory"/libhostpolicy*.dylib "$directory"/libhostfxr*.dylib \
    "$directory"/libcoreclr*.dylib "$directory"/libmono*.dylib; do
  if [[ -e "$candidate" || -L "$candidate" ]]; then
    echo "Managed runtime sidecar forbidden in strict macOS AOT output: $candidate" >&2
    exit 1
  fi
done
shopt -u nullglob nocaseglob
# Fail closed on runtime dynamic-link dependencies even when no managed
# sidecar lives next to this binary.
command -v otool >/dev/null || { echo 'Missing otool dependency auditor' >&2; exit 2; }
if ! dependencies="$(otool -L "$binary" 2>&1)"; then
  echo "Unable to inspect Mach-O linked dependencies: $binary" >&2
  exit 1
fi
if printf '%s\n' "$dependencies" | grep -Eiq 'lib(hostpolicy|hostfxr|coreclr|mono)[^/[:space:]]*\.dylib'; then
  echo "Managed runtime dynamic linkage forbidden in NativeAOT release: $binary" >&2
  exit 1
fi
echo "Verified standalone Mach-O ARM64 layout: $binary"
