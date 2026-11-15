#!/usr/bin/env bash
# Exact canonical Oreslang scalar fixture -> real Native AOT compiler +
# real Native AOT guest. Not a tensor/autograd/actor implementation claim.
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
source="$root/examples/actor-ml-scalar-step.ores"
expected="37dfb0ba856762dc656b628a085684558e5ac1d0392ccd6947d6ccdc35ed7d8e"
command -v sha256sum >/dev/null || { echo "sha256sum required" >&2; exit 2; }
# Lock down the exact test input before beginning a long Native AOT
# publication. Hashing a mutable checkout path only before compiling permits
# a replacement between hash admission and compiler file open.
umask 077
source_snapshot_dir="$(mktemp -d)"
trap 'rm -rf -- "$source_snapshot_dir"' EXIT
source_snapshot="$source_snapshot_dir/actor-ml-scalar-step.ores"
cp -- "$source" "$source_snapshot"
chmod 0400 -- "$source_snapshot"
actual="$(sha256sum -- "$source_snapshot")"
[[ "${actual%% *}" == "$expected" ]] || { echo "source identity mismatch" >&2; exit 1; }
cd "$root"
# This script must only accept a real compiler-and-application Native AOT
# publication; managed-only execution may not promote a guest parity claim.
bash scripts/publish-linux.sh "$source_snapshot" artifacts/apps/actor-ml-scalar
actual="$(sha256sum -- "$source_snapshot")"
[[ "${actual%% *}" == "$expected" ]] || { echo "source changed during Native AOT compilation" >&2; exit 1; }
app="$root/artifacts/apps/actor-ml-scalar/linux-x64/oreslang-app"
[[ -x "$app" ]] || { echo "missing native AOT guest executable" >&2; exit 1; }
set +e
output="$("$app" 2>"$root/artifacts/apps/actor-ml-scalar/native-guest.err")"
status=$?
set -e
[[ $status -eq 0 && "$output" == 42 ]] || {
  echo "NativeAOT Oreslang scalar guest must print exactly 42 (status $status)" >&2
  exit 1
}
[[ ! -s "$root/artifacts/apps/actor-ml-scalar/native-guest.err" ]] || {
  echo "Unexpected guest stderr" >&2; exit 1;
}
echo "NET_NATIVEAOT_ACTOR_ML_SCALAR_GUEST: PASS exact fixture SHA-256, stdout 42"
