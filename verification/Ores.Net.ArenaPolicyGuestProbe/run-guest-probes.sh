#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
dotnet --info
dotnet run -c Release --project verification/Ores.Net.StructuredBitAndProbe/Ores.Net.StructuredBitAndProbe.csproj
dotnet run -c Release --project verification/Ores.Net.ArenaPolicyGuestProbe/Ores.Net.ArenaPolicyGuestProbe.csproj
