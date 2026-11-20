#!/usr/bin/env bash
# Compiled .atto -> C# -> CoreCLR/NativeAOT guest preview. Not direct CIL.
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || { echo 'DOTNET_SDK_MISSING' >&2; exit 2; }
project=compiler/Atto.Cil.ResultPreview/Atto.Cil.ResultPreview.csproj
dotnet build "$project" -c Release
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for entry in \
  ok:'ATTO_RESULT=Ok(42)' \
  throw:'ATTO_RESULT=Err(bad input)' \
  existing_ok:'ATTO_RESULT=Ok(42)' \
  existing_err:'ATTO_RESULT=Err(existing error)' \
  direct_min:'ATTO_RESULT=Ok(-9223372036854775808)'; do
  case_name="${entry%%:*}"
  expected="${entry#*:}"
  target="$work/$case_name"
  mkdir -p "$target"
  dotnet run -c Release --no-build --project "$project" -- \
    "fixtures/result_preview/$case_name.atto" "$target/Program.cs" >"$target/compiler.out"
  grep -Fx 'ATTO_RESULT_PREVIEW_SOURCE_EMITTED=PASS' "$target/compiler.out"
  grep -Fx 'ATTO_RESULT_DIRECT_CIL_GUEST=NOT_EXECUTED' "$target/compiler.out"
  cp verification/Atto.ResultPreview.Guest.csproj "$target/Atto.ResultPreview.Guest.csproj"
  dotnet build "$target/Atto.ResultPreview.Guest.csproj" -c Release -o "$target/clr"
  dotnet "$target/clr/Atto.ResultPreview.Guest.dll" >"$target/clr.out"
  grep -Fx "$expected" "$target/clr.out"
  grep -Fx 'ATTO_RESULT_GUEST=CSHARP_TO_CIL_PREVIEW' "$target/clr.out"
  dotnet publish "$target/Atto.ResultPreview.Guest.csproj" -c Release \
    -r linux-x64 -p:PublishAot=true --self-contained true -o "$target/native"
  test -x "$target/native/Atto.ResultPreview.Guest"
  "$target/native/Atto.ResultPreview.Guest" >"$target/native.out"
  cmp "$target/clr.out" "$target/native.out"
done
echo 'ATTO_RESULT_PREVIEW_COMPILED_CLR_NATIVEAOT=PASS'
for entry in \
  unapproved_throw:E_EFFECT \
  trap_nonexistent:E_SYMBOL \
  unsupported_option:E_PARSE \
  unsupported_question:E_LEX \
  overflow:E_LITERAL \
  injection:E_STRING \
  host_keyword:E_NAME; do
  case_name="${entry%%:*}"
  expected="${entry#*:}"
  output="$work/$case_name/Program.cs"
  mkdir -p "$(dirname "$output")"
  set +e
  dotnet run -c Release --no-build --project "$project" -- \
    "fixtures/result_preview/negative/$case_name.atto" "$output" \
    >"$work/$case_name.out" 2>"$work/$case_name.err"
  status=$?
  set -e
  test "$status" -ne 0
  grep -Fx "$expected" "$work/$case_name.err"
  test ! -e "$output" || {
    echo "UNSAFE_OUTPUT_ARTIFACT=$case_name" >&2; exit 1;
  }
done
echo 'ATTO_RESULT_PREVIEW_NEGATIVE_NO_ARTIFACT=PASS'
echo 'ATTO_RESULT_DIRECT_CIL_GUEST=NOT_EXECUTED'
echo 'ATTO_RESULT_TENANT_VM_ISOLATION=NOT_EXECUTED'
