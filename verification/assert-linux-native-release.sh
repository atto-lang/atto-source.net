#!/usr/bin/env bash
# Must run after a successful PublishAot=true dotnet publish; ELF alone is not AOT proof.
set -Eeuo pipefail
if [[ $# -ne 1 ]]; then
  echo 'usage: bash verification/assert-linux-native-release.sh binary' >&2
  exit 2
fi
binary="$1"
[[ -f "$binary" && -x "$binary" && ! -L "$binary" ]] || {
  echo "Expected a regular native executable: $binary" >&2
  exit 1
}
file -b -- "$binary" | grep -Eq '^ELF 64-bit .* (pie executable|executable)' || {
  echo "Not a Linux ELF native executable: $binary" >&2
  exit 1
}
readelf -h -- "$binary" | grep -Eq 'Machine: *Advanced Micro Devices X86-64' || {
  echo "Unexpected ELF architecture: $binary" >&2
  exit 1
}
dir="$(dirname -- "$binary")"
if find "$dir" -maxdepth 1 -type f \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) | grep -q .; then
  echo "Managed runtime sidecar in native publish folder: $dir" >&2
  exit 1
fi
echo "Native ELF and release layout verified: $binary"
