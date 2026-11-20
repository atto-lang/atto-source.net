#!/usr/bin/env bash
# Host-only .NET/NativeAOT Result contract; MUST NOT be counted as .atto guest.
set -euo pipefail
cd "$(dirname "$0")/.."
project=tests/Atto.Cil.Result.Host/Atto.Cil.Result.Host.csproj
dotnet build "$project" -c Release
dotnet run --no-build -c Release --project "$project" | tee /tmp/atto-result-host-clr.out
grep -Fq 'ATTO_CIL_RESULT_GUEST=NOT_EXECUTED' /tmp/atto-result-host-clr.out
dotnet publish "$project" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-result-host-aot
/tmp/atto-result-host-aot/Atto.Cil.Result.Host | tee /tmp/atto-result-host-aot.out
cmp /tmp/atto-result-host-clr.out /tmp/atto-result-host-aot.out
echo ATTO_CIL_RESULT_HOST_CORECLR_NATIVEAOT=PASS
echo ATTO_CIL_RESULT_GUEST=NOT_EXECUTED
