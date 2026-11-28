#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
dotnet --info
dotnet run -c Release --project verification/Ores.Net.StructuredBitAndProbe/Ores.Net.StructuredBitAndProbe.csproj
test "$(git hash-object tests/arena-policy-differential-v1.mjs)" = f98055af7bb9b8952664946d2b2a1f3a735dc283
corpus="$(mktemp)"
trap 'rm -f "$corpus"' EXIT
node --input-type=module -e 'import {arenaPolicyBatches} from "./tests/arena-policy-differential-v1.mjs"; process.stdout.write(JSON.stringify(arenaPolicyBatches()));' > "$corpus"
dotnet run -c Release --project verification/Ores.Net.ArenaPolicyGuestProbe/Ores.Net.ArenaPolicyGuestProbe.csproj -- "$corpus"
