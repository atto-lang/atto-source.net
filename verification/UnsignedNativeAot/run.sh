#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
dotnet build "$here/Emitter/Emitter.csproj" -c Release
for row in max:0:18446744073709551615 divide:0:9223372036854775807 multiply:0:18446744073709551615 add-overflow:11:integer_overflow sub-underflow:11:integer_overflow mul-overflow:11:integer_overflow divide-zero:12:division_by_zero high-divisor:0:1 precedence:0:5 left-assoc:0:3 parentheses:0:20 overflow-first:11:integer_overflow zero-first:12:division_by_zero; do
  IFS=: read -r name status expected <<< "$row"
  case_dir="$work/$name"
  mkdir -p "$case_dir"
  dotnet run --no-build --project "$here/Emitter/Emitter.csproj" -c Release -- "$name" "$case_dir/OresGenerated.dll"
  test -s "$case_dir/OresGenerated.dll"
  dotnet publish "$here/Host/Host.csproj" -c Release -r linux-x64 -o "$case_dir/native" \
    -p:UnsignedGuestAssembly="$case_dir/OresGenerated.dll" \
    -p:BaseIntermediateOutputPath="$case_dir/obj/" -p:BaseOutputPath="$case_dir/bin/"
  exe="$case_dir/native/Host"
  test -x "$exe"
  readelf -h "$exe" | grep -q 'ELF64'
  # Do not accept a managed apphost accompanied by a managed entry assembly.
  test ! -e "$case_dir/native/Host.dll"
  set +e
  "$exe" >"$case_dir/out" 2>"$case_dir/err"
  actual=$?
  set -e
  test "$actual" -eq "$status"
  printf '%s\n' "$expected" >"$case_dir/expected"
  if [ "$status" -eq 0 ]; then
    cmp -s "$case_dir/out" "$case_dir/expected"
    test ! -s "$case_dir/err"
  else
    test ! -s "$case_dir/out"
    cmp -s "$case_dir/err" "$case_dir/expected"
  fi
  printf 'ATTO_U64_NATIVE_AOT|%s|%s|%s\n' "$name" "$status" "$expected"
done
