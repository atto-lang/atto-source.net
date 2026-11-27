#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
corpus="${ATTO_CONTEXT_CORPUS:?absolute corpus path required}"
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
mkdir -p artifacts
guest=artifacts/AttoGuest.dll
work="$(mktemp -d)"
trap 'rm -rf "$work"; rm -f "$guest"' EXIT
dotnet build "$compiler" -c Release
execute=0; reject=0; outside=0
for file in "$corpus"/pass/*.atto; do
  name="$(basename "$file" .atto)"
  blob="$(git hash-object "$file")"
  case "$name" in
    direct|immutable-alias|legacy-body) expected=42;;
    independent-bindings) expected=20;;
    multi-independent) expected=50;;
    snapshot-reuse) expected=41;;
    multi-snapshot) expected=34;;
    *) exit 2;;
  esac
  rm -f "$guest"
  status=0
  dotnet run --no-build -c Release --project "$compiler" -- "$file" "$guest" >"$work/out" 2>"$work/err" || status=$?
  if [[ "$status" == 2 && ! -e "$guest" && "$name" =~ ^(snapshot-reuse|multi-snapshot|legacy-body)$ ]]; then
    echo "SHARED_OUTSIDE_PROFILE $name $blob $(cat "$work/err")"; outside=$((outside+1)); continue
  fi
  test "$status" = 0
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  status=0; dotnet run --no-build -c Release --project "$host" || status=$?
  test "$status" = "$expected"
  echo "SHARED_EXECUTED_CORECLR $name $blob $status"
  if [[ "$name" == immutable-alias || "$name" == multi-independent ]]; then
    dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$work/native-$name"
    status=0; "$work/native-$name/Atto.Cil.Guest.Host" || status=$?
    test "$status" = "$expected"
    echo "SHARED_EXECUTED_NATIVEAOT $name $blob $status"
  fi
  execute=$((execute+1))
done
for file in "$corpus"/fail/*.atto; do
  name="$(basename "$file" .atto)"; blob="$(git hash-object "$file")"
  rm -f "$work/rejected.dll"
  status=0
  dotnet run --no-build -c Release --project "$compiler" -- "$file" "$work/rejected.dll" >"$work/out" 2>"$work/err" || status=$?
  test "$status" = 2
  test ! -e "$work/rejected.dll"
  test -s "$work/err"
  echo "SHARED_REJECT_UNCLASSIFIED $name $blob $(cat "$work/err")"; reject=$((reject+1))
done
echo "SHARED_SUMMARY executed=$execute rejectedUnclassified=$reject outside=$outside"
test "$((execute+outside))" = 7
test "$reject" = 13
