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
# Case-folding must not bypass sidecar denial on case-sensitive volumes.
printf 'MZ' > "$work/SHADOW.DLL"
if bash "$verifier" "$work/native"; then
  echo 'Verifier admitted a case-aliased managed DLL sidecar' >&2
  exit 1
fi
rm "$work/SHADOW.DLL"
printf 'fake runtime host' > "$work/LIBHOSTPOLICY.DYLIB"
if bash "$verifier" "$work/native"; then
  echo 'Verifier admitted a managed runtime host dylib' >&2
  exit 1
fi
rm "$work/LIBHOSTPOLICY.DYLIB"

# Independently test a Mach-O with a managed runtime dependency outside
# the executable directory; sidecar-only checks must not accept it.
mkdir -p "$work/runtime" "$work/linked"
printf 'int fake_hostpolicy(void) { return 7; }\n' > "$work/fake-runtime.c"
printf 'extern int fake_hostpolicy(void); int main(void) { return fake_hostpolicy(); }\n' > "$work/linked-main.c"
xcrun clang -arch arm64 -dynamiclib "$work/fake-runtime.c" \
  -Wl,-install_name,@rpath/libhostpolicy.dylib -o "$work/runtime/libhostpolicy.dylib"
xcrun clang -arch arm64 "$work/linked-main.c" \
  -L"$work/runtime" -lhostpolicy -Wl,-rpath,"$work/runtime" -o "$work/linked/native"
if bash "$verifier" "$work/linked/native"; then
  echo 'Verifier admitted dynamically linked managed runtime dylib' >&2
  exit 1
fi
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
# External and symlinked sources must fail before the fake SDK is invoked.
printf 'fnc main():int { return 42; }\n' > "$work/outside-source.ores"
ln -s "$work/outside-source.ores" "$staging/linked-source.ores"
for denied_source in "$work/outside-source.ores" "$staging/linked-source.ores"; do
  if output="$(PATH="$work/bin:$PATH" bash "$staging/scripts/publish-macos.sh" "$denied_source" artifacts/apps 2>&1)"; then
    echo "Unsafe AOT source unexpectedly admitted: $denied_source" >&2
    exit 1
  fi
  if [[ "$output" != *"Source must be a regular non-symlinked file inside this checkout"* ||
        "$output" == *DOTNET_INVOKED* ]]; then
    echo "Unsafe source did not fail closed before SDK invocation: $output" >&2
    exit 1
  fi
done
echo 'MACOS_ARM64_NET_NATIVE_AOT_RELEASE_GATES_SELFTEST_PASSED'
