#!/usr/bin/env bash
# Compile one admitted .ores program to generated CIL, then statically link
# the generated assembly into a real macOS ARM64 NativeAOT application.
# No interpreter, managed CLI fallback, dynamic guest IL loading, or runtime JIT.
set -Eeuo pipefail
if [[ $# -lt 1 || $# -gt 3 ]]; then
  echo 'usage: bash scripts/publish-macos.sh source.ores [app-output-root] [--structured]' >&2
  exit 2
fi
for tool in dotnet file lipo xcrun; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool" >&2; exit 2; }
done
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || {
  echo 'Strict osx-arm64 publish requires a native Apple Silicon build host' >&2
  exit 2
}
root="$(cd "$(dirname "$0")/.." && pwd -P)"
source_dir="$(cd "$(dirname "$1")" && pwd -P)"
source_file="$source_dir/$(basename "$1")"
[[ "$source_file" == "$root/"* && -f "$source_file" && ! -L "$source_file" ]] || {
  echo 'Source must be a regular non-symlinked file inside this checkout' >&2
  exit 2
}
output_root='artifacts/apps'
if [[ $# -ge 2 ]]; then output_root="$2"; fi
if [[ ! "$output_root" =~ ^artifacts(/[A-Za-z0-9_-]+)+$ ||
      "$output_root" =~ ^artifacts/(compiler|generated|evidence)(/|$) ]]; then
  echo 'app-output-root must be a separate artifacts/ output path' >&2
  exit 2
fi
mode=compile
if [[ $# -eq 3 ]]; then
  [[ "$3" == '--structured' ]] || {
    echo 'unsupported native AOT publish mode (expected --structured)' >&2
    exit 2
  }
  mode=compile-structured
fi
cd "$root"
rid=osx-arm64
compiler_dir="$root/artifacts/compiler/$rid"
app_dir="$root/$output_root/$rid"
generated_dir="$root/artifacts/generated/$rid"
generated="$generated_dir/OresGenerated.dll"
compiler="$compiler_dir/oresnet"
app="$app_dir/oreslang-app"
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj

# Check every existing ancestor before mkdir. Reject symlink redirection
# rather than writing private source products outside this checkout.
assert_safe_path() {
  local relative="$1" part current="$root"
  local -a parts
  IFS='/' read -r -a parts <<< "$relative"
  for part in "${parts[@]}"; do
    current="$current/$part"
    [[ ! -L "$current" ]] || {
      echo "Native AOT output escapes through symlink: $current" >&2
      exit 2
    }
  done
}
assert_safe_path artifacts/compiler/osx-arm64
assert_safe_path "$output_root/osx-arm64"
assert_safe_path artifacts/generated/osx-arm64
mkdir -p "$compiler_dir" "$app_dir" "$generated_dir"
assert_safe_path artifacts/compiler/osx-arm64
assert_safe_path "$output_root/osx-arm64"
assert_safe_path artifacts/generated/osx-arm64
rm -f "$compiler" "$app" "$generated"

dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj \
  -c Release -r "$rid" -o "$compiler_dir" -warnaserror
bash verification/assert-macos-native-release.sh "$compiler"
"$compiler" "$mode" "$source_file" --out "$generated"
[[ -s "$generated" && ! -L "$generated" ]] || {
  echo 'macOS native compiler did not emit a real generated CIL assembly' >&2
  exit 1
}
# Host intermediates cannot reuse a previously linked guest assembly.
dotnet clean "$host" -c Release -r "$rid" "-p:GeneratedAssembly=$generated"
dotnet publish "$host" -c Release -r "$rid" -o "$app_dir" \
  "-p:GeneratedAssembly=$generated" -warnaserror
bash verification/assert-macos-native-release.sh "$app"
echo 'MACOS_ARM64_NET_NATIVE_AOT_COMPILER_AND_APP_PUBLISHED'
