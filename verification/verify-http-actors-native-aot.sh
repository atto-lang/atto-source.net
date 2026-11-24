#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
rid="${1:?Usage: verify-http-actors-native-aot.sh RID}"
case "$rid" in
  win-*) executable=Ores.Net.HttpGuest.exe ;;
  *) executable=Ores.Net.HttpGuest ;;
esac
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
cd "$root"

source=verification/Ores.Net.HttpGuest/handlers.atto
compiler=compiler/Ores.Net.Compiler
generated="$work/HttpActors.g.cs"
"$dotnet" run -c Release --project "$compiler" -- emit-http-actors-csharp "$source" --out "$generated"

# Borrowed exchange aliases cannot enter the response suspension profile.
sed 's/await request.respond/val borrow HttpExchange loan = rt borrow request; await request.respond/' \
  "$source" >"$work/loan.atto"
if "$dotnet" run -c Release --no-build --project "$compiler" -- \
  emit-http-actors-csharp "$work/loan.atto" --out "$work/rejected.cs" >"$work/rejected.log" 2>&1; then
  echo "The HTTP compiler admitted a loan across response await" >&2
  exit 1
fi
test ! -e "$work/rejected.cs"
grep -Fq NET_HTTP_ACTOR_UNSUPPORTED "$work/rejected.log"

"$dotnet" publish verification/Ores.Net.HttpGuest -c Release -r "$rid" \
  -p:PublishAot=true -p:GeneratedHttpActors="$generated" -o "$work/native"
"$work/native/$executable"
