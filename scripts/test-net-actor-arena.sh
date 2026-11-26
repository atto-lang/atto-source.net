#!/usr/bin/env bash
set -euo pipefail
# Managed tests only; the GitHub workflow separately publishes and executes
# real NativeAOT binaries on both Linux and Windows, not synthetic receipts.
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'NOT_EXECUTED: dotnet SDK unavailable (never a PASS)' >&2
  exit 77
fi
dotnet run --configuration Release --project verification/Atto.Net.ActorRuntime.Tests
