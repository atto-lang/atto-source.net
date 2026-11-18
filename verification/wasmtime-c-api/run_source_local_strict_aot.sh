#!/usr/bin/env bash
# Manual, source-local execution on an isolated Linux x64 runner only.
# This intentionally does NOT authorize cross-organization source export.
# No automatic PR-triggered self-hosted workflow is created by this script.
set -euo pipefail
umask 077

if [[ $# -ne 1 || ! "$1" =~ ^[0-9a-f]{40}$ ]]; then
  echo 'usage: run_source_local_strict_aot.sh <exact-source-commit-sha>' >&2
  exit 64
fi
expected_head=$1
cd "$(dirname -- "${BASH_SOURCE[0]}")/../.."
[[ "$(uname -s)" == "Linux" && "$(uname -m)" == "x86_64" ]] || {
  echo 'requires isolated Linux x64 runner' >&2; exit 69;
}
for binary in git python3 dotnet bash file ldd sha256sum grep mktemp; do
  command -v "$binary" >/dev/null || { echo "missing tool: $binary" >&2; exit 69; }
done
actual_head=$(git rev-parse HEAD)
[[ "$actual_head" == "$expected_head" ]] || {
  echo "refusing source SHA mismatch: $actual_head" >&2; exit 65;
}
[[ -z "$(git status --porcelain --untracked-files=all)" ]] || {
  echo 'refusing dirty source checkout' >&2; exit 65;
}
actual_tree=$(git rev-parse 'HEAD^{tree}')
[[ "$actual_tree" =~ ^[0-9a-f]{40}$ ]] || exit 65
echo "SOURCE_LOCAL_EXACT_HEAD=$actual_head"
echo "SOURCE_LOCAL_EXACT_TREE=$actual_tree"

# These are structural and release-policy checks, not native sandbox proof.
python3 verification/test_wasmtime_strict_aot_source_contract.py
bash -n verification/wasmtime-c-api/build_compiler_free_50_0_2.sh
bash verification/wasmtime-c-api/test_preflight.sh

[[ "$(dotnet --version)" == 10.* ]] || {
  echo 'requires .NET SDK 10.x and Native AOT build toolchain' >&2; exit 69;
}
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

dotnet publish verification/Ores.Net.WasmtimeAotBuilder/Ores.Net.WasmtimeAotBuilder.csproj \
  -c Release -r linux-x64 -o "$work/builder" -p:PublishAot=true -p:SelfContained=true
dotnet publish verification/Ores.Net.WasmtimeAotRunner/Ores.Net.WasmtimeAotRunner.csproj \
  -c Release -r linux-x64 -o "$work/runner" -p:PublishAot=true -p:SelfContained=true

builder="$work/builder/oresnet-wasmtime-aot-builder"
runner="$work/runner/oresnet-wasmtime-aot-runner"
for executable in "$builder" "$runner"; do
  test -x "$executable"
  file "$executable" | grep -F 'ELF 64-bit'
  ! ldd "$executable" | grep -E '(libcoreclr|libhostfxr|libhostpolicy|libclrjit)'
  sha256sum "$executable"
done

# Vulnerable 50.0.1 synthetic CLI must deny use without explicit test opt-in.
set +e
env -u ORES_WASMTIME_SYNTHETIC_CI_ONLY "$builder" "$work/denied" \
  2>"$work/denied-builder.log"
builder_denied=$?
env -u ORES_WASMTIME_SYNTHETIC_CI_ONLY "$runner" \
  2>"$work/denied-runner.log"
runner_denied=$?
set -e
[[ "$builder_denied" -eq 78 && "$runner_denied" -eq 78 ]]
grep -Fx WASMTIME_SYNTHETIC_PROBE_NOT_AUTHORIZED "$work/denied-builder.log"
grep -Fx WASMTIME_SYNTHETIC_PROBE_NOT_AUTHORIZED "$work/denied-runner.log"

mkdir "$work/artifact"
ORES_WASMTIME_SYNTHETIC_CI_ONLY=1 "$builder" "$work/artifact" | tee "$work/builder.log"
grep -Fx NET_WASMTIME_TRUSTED_AOT_BUILDER_PASS "$work/builder.log"
[[ "$(find "$work/artifact" -type f | wc -l)" -eq 4 ]]
ORES_WASMTIME_SYNTHETIC_CI_ONLY=1 "$runner" \
  "$work/artifact/guest.manifest.json" \
  "$work/artifact/guest.manifest.sig" \
  "$work/artifact/guest.cwasm" \
  "$work/artifact/ephemeral-ci-public.pem" | tee "$work/runner.log"
grep -Fx NET_WASMTIME_STRICT_AOT_RUNTIME_PASS "$work/runner.log"
echo NET_WASMTIME_STRICT_AOT_E2E_PASS
echo "SOURCE_LOCAL_EXACT_HEAD=$actual_head"
echo "SOURCE_LOCAL_EXACT_TREE=$actual_tree"
