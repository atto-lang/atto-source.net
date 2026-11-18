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
for tool in git cargo rustc sha256sum file nm cc; do
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

# Use a fresh, dedicated Cargo target tree inside the checked-empty output
# directory. Without this, source_dir/target can hold older versions or
# compiler-enabled libwasmtime binaries that make provenance ambiguous.
# Never honor an inherited CARGO_TARGET_DIR pointing outside this receipt.
readonly trusted_target_dir="$output_dir/cargo-target"
export CARGO_TARGET_DIR="$trusted_target_dir"
export CARGO_INCREMENTAL=0

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
readonly lib="$trusted_target_dir/release/libwasmtime.so"
[[ -s "$lib" ]] || { echo 'missing compiler-free C-API shared library' >&2; exit 70; }
file "$lib" | grep -F 'ELF 64-bit' >/dev/null || {
  echo 'expected Linux native ELF shared library' >&2; exit 70;
}
# The compiler-free data plane must retain enough C ABI functionality to
# deserialize *previously trusted* native modules, create independent Stores,
# instantiate guests, and enforce fuel limits. Merely exporting an Engine
# constructor is not sufficient to make a useful tenant runtime.
nm -D --defined-only "$lib" | awk '{print $NF}' > "$output_dir/c-api-exports.txt"
for required_symbol in \
  wasmtime_module_deserialize \
  wasmtime_store_new \
  wasmtime_instance_new \
  wasmtime_context_set_fuel; do
  if ! grep -Fxq "$required_symbol" "$output_dir/c-api-exports.txt"; then
    echo "required compiler-free C ABI symbol missing: $required_symbol" >&2
    exit 70
  fi
done
if ! grep -Eq '^(wasmtime_engine_new|wasm_engine_new)$' "$output_dir/c-api-exports.txt"; then
  echo 'required engine entrypoint missing from C-API library' >&2
  exit 70
fi

# The compiler-free runtime library must not export the raw-Wasm compiler
# entrypoint (upstream guards it with WASMTIME_FEATURE_COMPILER).
if grep -Fxq wasmtime_module_new "$output_dir/c-api-exports.txt"; then
  echo 'raw-Wasm compiler entrypoint unexpectedly exported' >&2
  exit 65
fi
cp -- "$lib" "$output_dir/libwasmtime.so"

# Run a public C ABI smoke against the *actual* compiler-free native library.
# It creates two empty Stores without deserializing untrusted native code.
readonly script_dir=$(cd -- "$(dirname -- "$0")" && pwd -P)
cc -std=c11 -Wall -Wextra -Werror \
  -I "$source_dir/crates/c-api/include" \
  "$script_dir/probe_store_abi.c" \
  -L "$output_dir" -Wl,-rpath,"$output_dir" -lwasmtime \
  -o "$output_dir/probe_store_abi"
"$output_dir/probe_store_abi" > "$output_dir/store-abi-smoke.log"
grep -Fxq WASMTIME_COMPILER_FREE_C_ABI_STORE_PASS \
  "$output_dir/store-abi-smoke.log" || {
    echo 'native C API engine/store lifecycle smoke missing PASS' >&2
    exit 70
  }

{
  echo "upstream_repo=bytecodealliance/wasmtime"
  echo "release_tag=$RELEASE_TAG"
  echo "git_commit=$head_sha"
  echo "git_tree=$(git -C "$source_dir" rev-parse HEAD^{tree})"
  echo "cargo_target_dir=$trusted_target_dir"
  echo 'cargo_flags=--release --no-default-features --features disable-logging --locked'
  echo "rustc=$(rustc --version)"
  echo "cargo=$(cargo --version)"
  sha256sum "$output_dir/libwasmtime.so" \
    "$output_dir/cargo-feature-tree.txt" "$output_dir/c-api-exports.txt" \
    "$output_dir/probe_store_abi" "$output_dir/store-abi-smoke.log"
} > "$output_dir/build-receipt.txt"
echo 'WASMTIME_50_0_2_COMPILER_FREE_C_API_BUILD_CANDIDATE_PASS'
