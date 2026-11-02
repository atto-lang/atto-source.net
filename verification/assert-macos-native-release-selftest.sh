#!/usr/bin/env bash
# Local macOS ARM64 source-free negative controls. A C Mach-O proves the
# header verifier only; ONLY the separate NativeAOT guest run proves AOT.
set -Eeuo pipefail
root="$(cd "$(dirname "$0")/.." && pwd -P)"
verifier="$root/verification/assert-macos-native-release.sh"
publisher="$root/scripts/publish-macos.sh"
bash -n "$verifier" "$publisher"
work="$(mktemp -d "${TMPDIR:-/tmp}/ores-net-macos-aot.XXXXXXXX")"
trap 'rm -rf "$work"' EXIT
printf 'int main(void) { return 0; }\n' > "$work/native.c"
xcrun clang -arch arm64 -o "$work/native" "$work/native.c"
bash "$verifier" "$work/native"
printf 'MZ' > "$work/managed.dll"
if bash "$verifier" "$work/native"; then
  echo 'Verifier admitted a managed DLL sidecar' >&2
  exit 1
fi
rm "$work/managed.dll"
printf '{}' > "$work/unwanted.runtimeconfig.json"
if bash "$verifier" "$work/native"; then
  echo 'Verifier admitted a runtime config sidecar' >&2
  exit 1
fi
rm "$work/unwanted.runtimeconfig.json"
ln -s "$work/native" "$work/alias"
if bash "$verifier" "$work/alias"; then
  echo 'Verifier admitted a symlinked executable' >&2
  exit 1
fi
printf '#!/bin/sh\nexit 0\n' > "$work/wrapper"
chmod +x "$work/wrapper"
if bash "$verifier" "$work/wrapper"; then
  echo 'Verifier admitted a shell wrapper' >&2
  exit 1
fi

# Unsafe output destinations must be rejected before SDK invocation.
staging="$work/staging"
mkdir -p "$staging/scripts" "$staging/artifacts" "$work/bin"
cp "$publisher" "$staging/scripts/publish-macos.sh"
printf 'fnc main():int { return 42; }\n' > "$staging/fixture.ores"
cat > "$work/bin/dotnet" <<'SDK'
#!/usr/bin/env bash
echo DOTNET_INVOKED >&2
exit 88
SDK
chmod +x "$work/bin/dotnet"
assert_rejected() {
  local expected="$1" output
  shift
  if output="$(PATH="$work/bin:$PATH" bash "$staging/scripts/publish-macos.sh" "$staging/fixture.ores" "$@" 2>&1)"; then
    echo 'Unsafe publisher invocation unexpectedly succeeded' >&2
    exit 1
  fi
  if [[ "$output" != *"$expected"* || "$output" == *DOTNET_INVOKED* ]]; then
    echo "Publisher did not fail closed before SDK use: $output" >&2
    exit 1
  fi
}
assert_rejected 'separate artifacts/' 'artifacts/../outside'
assert_rejected 'separate artifacts/' 'artifacts//apps'
assert_rejected 'separate artifacts/' 'artifacts/generated'
assert_rejected 'separate artifacts/' '/tmp/outside'
assert_rejected 'unsupported native AOT publish mode' 'artifacts/structured' '--bad'
mkdir -p "$work/outside"
ln -s "$work/outside" "$staging/artifacts/apps"
assert_rejected 'escapes through symlink'
[[ ! -e "$work/outside/osx-arm64" ]] || { echo 'Escaped staging tree' >&2; exit 1; }
rm "$staging/artifacts/apps"
mkdir -p "$staging/artifacts/apps"
ln -s "$work/outside" "$staging/artifacts/apps/redirect"
assert_rejected 'escapes through symlink' 'artifacts/apps/redirect'
[[ ! -e "$work/outside/osx-arm64" ]] || { echo 'Escaped staging tree' >&2; exit 1; }
echo 'MACOS_ARM64_NET_NATIVE_AOT_RELEASE_GATES_SELFTEST_PASSED'
