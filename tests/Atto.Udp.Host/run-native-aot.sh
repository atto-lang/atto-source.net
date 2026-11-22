#!/usr/bin/env bash
# Real .NET host UDP smoke + native AOT. This is NOT compiled Atto guest code.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
proj="$root/tests/Atto.Udp.Host/Atto.Udp.Host.csproj"
rid="${ATTO_UDP_RID:-linux-x64}"
if ! command -v dotnet >/dev/null; then
  echo "NOT_EXECUTED: dotnet SDK missing" >&2
  exit 75
fi
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
dotnet --version
dotnet run --project "$proj" --configuration Release
dotnet publish "$proj" --configuration Release -r "$rid" --self-contained true \
  -p:PublishAot=true -p:InvariantGlobalization=true \
  --output "$scratch/aot"
test -x "$scratch/aot/Atto.Udp.Host"
"$scratch/aot/Atto.Udp.Host"
echo "UDP_DOTNET_HOST_AND_NATIVEAOT_EXECUTED=true"
