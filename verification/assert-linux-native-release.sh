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
# Avoid pipefail/SIGPIPE from early-exiting grep -q against multi-line readelf.
# Parsing readelf is a supplemental architecture check, not AOT proof.
elf_header="$(readelf -h -- "$binary")" || {
  echo "Cannot read ELF architecture: $binary" >&2
  exit 1
}
grep -Eq 'Machine: *Advanced Micro Devices X86-64' <<< "$elf_header" || {
  echo "Unexpected ELF architecture: $binary" >&2
  exit 1
}
dir="$(dirname -- "$binary")"
# -type f alone misses symlinked managed DLLs; -iname rejects mixed-case
# sidecars. Capture find's result without a pipe to avoid grep -q SIGPIPE
# making a true positive look like a clean folder under pipefail.
sidecar="$(find "$dir" -maxdepth 1 \( -type f -o -type l \) \
  \( -iname '*.dll' -o -iname '*.deps.json' -o -iname '*.runtimeconfig.json' \) \
  -print -quit)" || {
  echo "Unable to inspect native release directory: $dir" >&2
  exit 1
}
if [[ -n "$sidecar" ]]; then
  echo "Managed runtime sidecar in native publish folder: $sidecar" >&2
  exit 1
fi
echo "Native ELF and release layout verified: $binary"
