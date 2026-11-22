#!/usr/bin/env bash
# Real .NET CIL compiler + guest-host admission, NOT std/time class execution.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dotnet restore compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj \
  -p:TargetFramework=net10.0 -p:TargetFrameworks=net10.0 --force -v:q
dotnet build compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj \
  -c Release -p:TargetFramework=net10.0 -p:TargetFrameworks=net10.0 --no-restore -v:q
compiler="$PWD/compiler/Atto.Cil.Compiler/bin/Release/net10.0/Atto.Cil.Compiler.dll"
test -s "$compiler"
printf 'pub fnc main(): int { return 42; }\n' > "$work/scalar.atto"
dotnet "$compiler" "$work/scalar.atto" "$work/ScalarGuest.dll" > "$work/positive.log"
grep -q '^ATTO_DIRECT_CIL_PASS functions=1$' "$work/positive.log"
test -s "$work/ScalarGuest.dll"
mkdir -p artifacts
cp "$work/ScalarGuest.dll" artifacts/AttoGuest.dll
dotnet restore tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj \
  -p:TargetFramework=net10.0 -p:TargetFrameworks=net10.0 --force -v:q
dotnet build tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj \
  -c Release -p:TargetFramework=net10.0 -p:TargetFrameworks=net10.0 --no-restore -v:q
set +e
dotnet "$PWD/tests/Atto.Cil.Guest.Host/bin/Release/net10.0/Atto.Cil.Guest.Host.dll"
status=$?
set -e
test "$status" -eq 42 || { echo "SCALAR_GUEST_WRONG_EXIT=$status"; exit 1; }
echo 'NET_REAL_SCALAR_CIL_GUEST_PASS exit=42'
for kind in class_module import_module attempted_sleep; do
  case "$kind" in
    class_module)
      cp stdlib/std/time.atto "$work/input.atto" ;;
    import_module)
      printf 'import module time from "std/time"; pub fnc main(): int { return 42; }\n' > "$work/input.atto" ;;
    attempted_sleep)
      printf 'pub fnc main(): int { return time.sleep(100); }\n' > "$work/input.atto" ;;
  esac
  rm -f "$work/UntrustedTime.dll"
  set +e
  dotnet "$compiler" "$work/input.atto" "$work/UntrustedTime.dll" > "$work/denial.log" 2>&1
  result=$?
  set -e
  test "$result" -ne 0 || { echo "TIME_UNSUPPORTED_FALSE_ACCEPT $kind"; exit 1; }
  test ! -e "$work/UntrustedTime.dll" || { echo "TIME_UNSUPPORTED_EMITTED_GUEST $kind"; exit 1; }
  grep -Eq 'E_(LEX|PARSE|NAME|TYPE|UNSUPPORTED)' "$work/denial.log" || {
    echo "TIME_UNSUPPORTED_NO_TYPED_DIAGNOSTIC $kind"
    cat "$work/denial.log"
    exit 1
  }
  echo "NET_TIME_NEGATIVE_ADMISSION_PASS $kind"
done
