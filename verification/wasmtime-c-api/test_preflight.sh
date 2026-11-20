#!/usr/bin/env bash
set -euo pipefail

# Exercised on a local host without cargo, rustc or .NET; this deliberately
# tests only rejection before native compilation, never a Wasmtime build.
readonly script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
readonly builder="$script_dir/build_compiler_free_50_0_2.sh"
bash -n "$builder"

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/not-git" "$work/output-busy" "$work/output-empty"
printf 'reserved\n' > "$work/output-busy/existing.txt"

# Preflight must have all requested tool names available, but must never
# execute Cargo/Rust when the upstream checkout is unapproved. The stubs
# deliberately fail if reached, and also leave a trace for verification.
cat > "$work/bin/cargo" <<'SH'
#!/usr/bin/env sh
printf 'cargo invoked\n' >> "$CARGO_SENTINEL"
exit 99
SH
cat > "$work/bin/rustc" <<'SH'
#!/usr/bin/env sh
printf 'rustc invoked\n' >> "$CARGO_SENTINEL"
exit 99
SH
chmod +x "$work/bin/cargo" "$work/bin/rustc"
export PATH="$work/bin:$PATH"
export CARGO_SENTINEL="$work/native-compiler-invoked"

expect_exit() {
  local expected=$1
  shift
  local observed=0
  "$@" > "$work/last-stdout" 2> "$work/last-stderr" || observed=$?
  if [[ "$observed" != "$expected" ]]; then
    printf 'expected exit %s, got %s\n' "$expected" "$observed" >&2
    cat "$work/last-stderr" >&2
    exit 1
  fi
  [[ ! -e "$CARGO_SENTINEL" ]] || {
    echo 'unapproved input invoked native compiler' >&2
    exit 1
  }
}

# Invalid invocation is rejected before attempting toolchain discovery.
expect_exit 64 bash "$builder"
# Without a .git directory, never compile even though output is empty.
expect_exit 65 bash "$builder" "$work/not-git" "$work/output-empty"

# Use a local throwaway Git repository, not private Atto source. Its HEAD
# will intentionally differ from the pinned official Wasmtime release SHA.
git init -q "$work/foreign-checkout"
git -C "$work/foreign-checkout" -c user.name=Preflight \
  -c user.email=preflight@example.invalid commit --allow-empty -qm seed
git -C "$work/foreign-checkout" tag v50.0.2

expect_exit 65 bash "$builder" "$work/foreign-checkout" "$work/output-empty"
# Output files must never be overwritten, even for a clean input checkout.
expect_exit 65 bash "$builder" "$work/foreign-checkout" "$work/output-busy"

echo 'WASMTIME_COMPILER_FREE_PREFLIGHT_PASS'
