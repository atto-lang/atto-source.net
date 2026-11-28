#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
cd "$root"
project=verification/Ores.Net.ControlPlaneArenaProbe/Ores.Net.ControlPlaneArenaProbe.csproj
dotnet --info
dotnet run -c Release --project "$project" -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false
dotnet publish "$project" -c Release -r linux-x64 -o "$out"
file "$out/Ores.Net.ControlPlaneArenaProbe" | grep -F 'ELF 64-bit'
"$out/Ores.Net.ControlPlaneArenaProbe"
