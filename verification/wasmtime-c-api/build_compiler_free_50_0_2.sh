#!/usr/bin/env bash
set -euo pipefail

# Trusted build-control-plane candidate, NOT a production shipping path.
# Only a public, reviewed upstream checkout is accepted. Do NOT copy private
# Atto/Oreslang source into this build workspace or rely on cross-org tokens.
# Source identity: bytecodealliance/wasmtime tag v50.0.2, immutable release SHA.
readonly RELEASE_SHA=f9a93b049a23bef9a33cfa3888f32af33a178749
readonly RELEASE_TAG=v50.0.2

if [[ $# -ne 2 ]]; then
  echo 'usage: build_compiler_free_50_0_2.sh <trusted-wasmtime-checkout> <empty-output-dir>' >&2
  exit 64
fi
for tool in git cargo rustc sha256sum file nm; do
  command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 69; }
done
source_dir=$(cd -- "$1" && pwd -P)
mkdir -p -- "$2"
output_dir=$(cd -- "$2" && pwd -P)
[[ -d "$source_dir/.git" || -f "$source_dir/.git" ]] || {
  echo 'expected a pinned upstream Git checkout' >&2; exit 65;
}
[[ -z $(find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit) ]] || {
  echo 'output directory must be empty' >&2; exit 65;
}
head_sha=$(git -C "$source_dir" rev-parse --verify HEAD)
tag_sha=$(git -C "$source_dir" rev-parse --verify "refs/tags/${RELEASE_TAG}^{commit}")
[[ "$head_sha" == "$RELEASE_SHA" && "$tag_sha" == "$RELEASE_SHA" ]] || {
  echo 'upstream Wasmtime release SHA/tag mismatch' >&2; exit 65;
}
[[ -z $(git -C "$source_dir" status --porcelain --untracked-files=all) ]] || {
  echo 'upstream checkout has unreviewed modifications/untracked files' >&2; exit 65;
}

# C API minimal embedding documented upstream. No Cranelift/Winch compiler.
# This is NOT proof that the existing .NET NuGet 50.0.1 binding can link
# against this library. Native ABI and .cwasm serialization compatibility
# must be proven before replacing that native dependency.
(
  cd -- "$source_dir"
  cargo tree -p wasmtime-c-api -e features --no-default-features \
    --features disable-logging --locked > "$output_dir/cargo-feature-tree.txt"
)
if grep -E 'wasmtime-(cranelift|winch)|feature "(cranelift|winch)"' \
  "$output_dir/cargo-feature-tree.txt"; then
  echo 'compiler feature present in C-API feature tree' >&2
  exit 65
fi
(
  cd -- "$source_dir"
  cargo build -p wasmtime-c-api --release --no-default-features \
    --features disable-logging --locked
)
readonly lib="$source_dir/target/release/libwasmtime.so"
[[ -s "$lib" ]] || { echo 'missing compiler-free C-API shared library' >&2; exit 70; }
file "$lib" | grep -F 'ELF 64-bit' >/dev/null || {
  echo 'expected Linux native ELF shared library' >&2; exit 70;
}
nm -D --defined-only "$lib" | grep -Eq ' (wasmtime_engine_new|wasm_engine_new)$' || {
  echo 'required engine entrypoint missing from C-API library' >&2; exit 70;
}
cp -- "$lib" "$output_dir/libwasmtime.so"
{
  echo "upstream_repo=bytecodealliance/wasmtime"
  echo "release_tag=$RELEASE_TAG"
  echo "git_commit=$head_sha"
  echo "git_tree=$(git -C "$source_dir" rev-parse HEAD^{tree})"
  echo 'cargo_flags=--release --no-default-features --features disable-logging --locked'
  echo "rustc=$(rustc --version)"
  echo "cargo=$(cargo --version)"
  sha256sum "$output_dir/libwasmtime.so" "$output_dir/cargo-feature-tree.txt"
} > "$output_dir/build-receipt.txt"
echo 'WASMTIME_50_0_2_COMPILER_FREE_C_API_BUILD_CANDIDATE_PASS'
