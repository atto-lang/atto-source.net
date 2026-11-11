#!/usr/bin/env bash
# Exact guest .atto source -> structured CFG -> build-time CIL -> .NET NativeAOT.
# This is a real target execution gate; unlike host CIL evaluators, it cannot
# be marked passed without a compiled executable running to completion.
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
command -v dotnet >/dev/null || { echo "dotnet SDK missing" >&2; exit 2; }
command -v file >/dev/null || { echo "file command missing" >&2; exit 2; }
work="$(mktemp -d "${TMPDIR:-/tmp}/atto-net-equality.XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT
generated="$work/OresGenerated.dll"
source="$root/examples/atto-neq-nativeaot.atto"
negative="$root/examples/atto-neq-mixed-bool-reject.atto"
rejected="$work/rejected/OresGenerated.dll"
expected_blob="0e69c04a8a15c3da924183656da10ab7dcdeb9bd"
test ! -L "$source" || { echo "Symlinked guest input forbidden" >&2; exit 1; }
test "$(git hash-object --no-filters "$source")" = "$expected_blob" || {
  echo "Canonical LLVM/.NET guest source bytes changed" >&2; exit 1;
}
test "$(git rev-parse 'HEAD:examples/atto-neq-nativeaot.atto')" = "$expected_blob" || {
  echo "Guest source is not pinned to exact checked-out HEAD" >&2; exit 1;
}

dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -- check-structured "$source"
dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -- compile-structured "$source" --out "$generated"
test -s "$generated" || { echo "No guest CIL" >&2; exit 1; }
# Fail-closed negative checks: exit 1 plus source-specific diagnostics.
# Merely failing because an output directory is missing, an SDK is absent, or
# a compiler crashed cannot be credited as a semantic rejection.
mkdir -p "$(dirname -- "$rejected")"
for case_id in mixed-bool split suffix; do
  case "$case_id" in
    mixed-bool) bad="$negative"; expected="Equality requires int operands" ;;
    split) bad="$root/examples/atto-neq-split-reject.atto"; expected="Invalid character at offset" ;;
    suffix) bad="$root/examples/atto-neq-suffix-reject.atto"; expected="Invalid character at offset" ;;
  esac
  test ! -L "$bad" || { echo "Symlinked negative source" >&2; exit 1; }
  relative="${bad#"$root"/}"
  test "$(git hash-object --no-filters "$bad")" = "$(git rev-parse "HEAD:$relative")" || {
    echo "Negative source bytes not checked-in at exact HEAD: $case_id" >&2; exit 1;
  }
  rm -f -- "$rejected"
  set +e
  dotnet run --project compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -- \
    compile-structured "$bad" --out "$rejected" > "$work/$case_id.stdout" 2> "$work/$case_id.stderr"
  exit_code=$?
  set -e
  if [[ "$exit_code" -ne 1 ]] || ! grep -Fq "$expected" "$work/$case_id.stderr"; then
    printf 'Negative %s did not fail with expected compiler diagnostic (exit %s)\n' "$case_id" "$exit_code" >&2
    cat "$work/$case_id.stderr" >&2
    exit 1
  fi
  test ! -s "$work/$case_id.stdout" || { echo "Negative emitted unexpected stdout" >&2; exit 1; }
  test ! -e "$rejected" || { echo "Rejected source produced CIL: $case_id" >&2; exit 1; }
done
sha256sum "$generated"

project="host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj"
output="$work/published"
dotnet clean "$project" -c Release -r linux-x64 "-p:GeneratedAssembly=$generated"
dotnet publish "$project" -c Release -r linux-x64 -o "$output" "-p:GeneratedAssembly=$generated" -warnaserror

app="$output/oreslang-app"
test -x "$app" || { echo "Missing NativeAOT guest executable" >&2; exit 1; }
file "$app" | grep -q 'ELF 64-bit' || { echo "Not native ELF" >&2; exit 1; }
test ! -e "$output/oreslang-app.runtimeconfig.json" || { echo "Managed runtimeconfig sidecar not permitted" >&2; exit 1; }
test ! -e "$output/OresGenerated.dll" || { echo "Generated guest DLL must be statically linked" >&2; exit 1; }
set +e
result="$("$app")"
status=$?
set -e
if [[ "$status" -ne 0 || "$result" != '42' ]]; then
  printf 'NativeAOT Atto equality mismatch: exit=%s output=%q (expected 42)\n' "$status" "$result" >&2
  exit 1
fi
sha256sum "$app"
echo 'NativeAOT AttoLang eq/neq/!eq guest execution OK: 42; 3 compiler-negative controls no-artifact'
