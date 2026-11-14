#!/usr/bin/env bash
# Fast source-free regression tests: do not need the .NET SDK or source transfer.
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "$0")/.." && pwd -P)"
verifier="$root/verification/assert-linux-native-release.sh"
publisher="$root/scripts/publish-linux.sh"
bash -n "$verifier" "$publisher"
work="$(mktemp -d "${TMPDIR:-/tmp}/ores-net-aot-gates.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT

# Tests the actual x64 ELF verifier, not a synthetic pass marker.
cp /bin/true "$work/native"
bash "$verifier" "$work/native"
printf 'MZ' > "$work/unexpected.dll"
if bash "$verifier" "$work/native"; then
  echo 'Accepted managed DLL sidecar' >&2
  exit 1
fi
rm -- "$work/unexpected.dll"
ln -s native "$work/link"
if bash "$verifier" "$work/link"; then
  echo 'Accepted a symlinked executable' >&2
  exit 1
fi
printf '#!/bin/sh\nexit 0\n' > "$work/fake"
chmod +x "$work/fake"
if bash "$verifier" "$work/fake"; then
  echo 'Accepted a shell wrapper as a native image' >&2
  exit 1
fi

# Verify output-path rejection before any dotnet execution, on an isolated
# temporary copy of the publisher with a fake SDK in PATH. This cannot
# compile or inspect private Oreslang source.
staging="$work/staging"
mkdir -p "$staging/scripts" "$staging/verification" "$staging/artifacts" "$work/bin"
cp "$publisher" "$staging/scripts/publish-linux.sh"
printf 'source fixture\n' > "$staging/fixture.ores"
cat > "$work/bin/dotnet" <<'SDK'
#!/usr/bin/env bash
printf 'DOTNET_INVOKED\n' >&2
exit 88
SDK
chmod +x "$work/bin/dotnet"

assert_rejected() {
  local output expected="$1"
  shift
  if output="$(PATH="$work/bin:$PATH" bash "$staging/scripts/publish-linux.sh" "$staging/fixture.ores" "$@" 2>&1)"; then
    echo 'Rejected publish invocation succeeded unexpectedly' >&2
    exit 1
  fi
  if [[ "$output" != *"$expected"* || "$output" == *DOTNET_INVOKED* ]]; then
    echo "Failed to reject path before dotnet invocation: $output" >&2
    exit 1
  fi
}
assert_rejected 'separate artifacts/' 'artifacts/../escape'
assert_rejected 'separate artifacts/' 'artifacts/compiler'
assert_rejected 'separate artifacts/' 'artifacts//apps'
assert_rejected 'separate artifacts/' '/tmp/escape'
assert_rejected 'unsupported native AOT publish mode' 'artifacts/structured' '--unexpected'
mkdir -p "$work/escaped"
ln -s "$work/escaped" "$staging/artifacts/apps"
assert_rejected 'canonical checkout path'
[[ ! -e "$work/escaped/linux-x64" ]] || { echo 'Directory escaped checkout' >&2; exit 1; }
rm -- "$staging/artifacts/apps"
mkdir -p "$staging/artifacts/apps"
ln -s "$work/escaped" "$staging/artifacts/apps/alias"
assert_rejected 'canonical checkout path' 'artifacts/apps/alias'
[[ ! -e "$work/escaped/linux-x64" ]] || { echo 'Directory escaped checkout' >&2; exit 1; }
echo 'LINUX_NET_NATIVE_AOT_RELEASE_GATES_SELFTEST_PASSED'
