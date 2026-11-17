#!/usr/bin/env bash
# Both source compiler and generated application must publish as Native AOT.
set -Eeuo pipefail

if [[ $# -lt 1 || $# -gt 4 ]]; then
  echo 'usage: bash scripts/publish-linux.sh source.ores [app-output-root] [--structured] [--interop-math-i64]' >&2
  exit 2
fi
for tool in dotnet file readelf realpath; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool" >&2; exit 2; }
done
root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
source_file="$(realpath -- "$1")"
[[ -f "$source_file" ]] || { echo "missing source: $source_file" >&2; exit 2; }
output_root='artifacts/apps'
if [[ $# -ge 2 ]]; then output_root="$2"; fi
# The experimental structured lane must be explicitly requested. Never
# reinterpret unsupported source as structured code in the scalar release.
compile_mode=compile
if [[ $# -ge 3 ]]; then
  if [[ "$3" != '--structured' ]]; then
    echo 'unsupported native AOT publish mode (expected --structured)' >&2
    exit 2
  fi
  compile_mode=compile-structured
fi
interop_math_i64=false
if [[ $# -eq 4 ]]; then
  if [[ "$4" != '--interop-math-i64' || "$compile_mode" != 'compile-structured' ]]; then
    echo 'interop-math-i64 requires the explicit structured import lane' >&2
    exit 2
  fi
  interop_math_i64=true
fi
# Reject empty segments, dot components and critical build/evidence directories.
if [[ ! "$output_root" =~ ^artifacts(/[a-zA-Z0-9_-]+)+$ ||
      "$output_root" =~ ^artifacts/(compiler|generated|evidence)(/|$) ]]; then
  echo 'app-output-root must be a separate artifacts/ output path' >&2
  exit 2
fi
cd "$root"
rid=linux-x64
compiler_dir="$root/artifacts/compiler/$rid"
app_dir="$root/$output_root/$rid"
generated="$root/artifacts/generated/$rid/OresGenerated.dll"
compiler="$compiler_dir/oresnet"
app="$app_dir/oreslang-app"
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
# Check *before mkdir*: otherwise a preexisting artifacts/ symlink can
# cause even the preliminary directory creation to escape the checkout.
# -m canonicalizes missing components without writing to them.
assert_canonical_outputs() {
  local mode="$1" directory actual
  for directory in "$root/artifacts" "$compiler_dir" "$app_dir" "$(dirname -- "$generated")"; do
    actual="$(realpath "$mode" -- "$directory")"
    if [[ "$actual" != "$directory" ]]; then
      echo "Native AOT output directory is not a canonical checkout path: $directory" >&2
      exit 2
    fi
  done
}
assert_canonical_outputs -m
mkdir -p "$compiler_dir" "$app_dir" "$(dirname -- "$generated")"
# Verify again after creation, before deleting/replacing any release inputs.
assert_canonical_outputs -e
# Prevent a previous valid binary masking a failed rebuild.
rm -f -- "$compiler" "$app" "$generated"

dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj \
  -c Release -r "$rid" -o "$compiler_dir" -warnaserror
bash verification/assert-linux-native-release.sh "$compiler"
if [[ "$interop_math_i64" == true ]]; then
  "$compiler" "$compile_mode" "$source_file" --out "$generated" --capability interop.dotnet.math-i64
else
  "$compiler" "$compile_mode" "$source_file" --out "$generated"
fi
[[ -s "$generated" ]] || { echo 'native compiler did not emit CIL' >&2; exit 1; }
# Stable OresGenerated assembly identity requires clearing previous host inputs.
dotnet clean "$host" -c Release -r "$rid" "-p:GeneratedAssembly=$generated"
dotnet publish "$host" -c Release -r "$rid" -o "$app_dir" \
  "-p:GeneratedAssembly=$generated" -warnaserror
bash verification/assert-linux-native-release.sh "$app"
# For the known examples/hello.ores generated entry (14), run the actual
# standalone Native AOT binary, not a framework-dependent test host.
if [[ "${ORES_NET_ISOLATE_SMOKE:-0}" == "1" ]]; then
  "$app" --isolate-smoke
fi
echo 'LINUX_NET_NATIVE_AOT_COMPILER_AND_APP_PUBLISHED'
