#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Execute this gate in a real dotnet SDK runner; source-only runners cannot
# establish NativeAOT or CIL guest parity.
for fixture in brace-continue keyword-continue keyword-failure; do
  dotnet run --project compiler/Ores.Net.Compiler -- check-structured "tests/fixtures/guard-dual-form-v1/${fixture}.atto"
done
for fixture in reject-fallthrough reject-mixed reject-nonbool; do
  if dotnet run --project compiler/Ores.Net.Compiler -- check-structured "tests/fixtures/guard-dual-form-v1/${fixture}.atto"; then
    echo "Guard negative unexpectedly compiled: ${fixture}" >&2
    exit 1
  fi
done
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
dotnet run --project compiler/Ores.Net.Compiler -- compile-structured \
  tests/fixtures/guard-dual-form-v1/keyword-continue.atto --out "$tmp/OresGenerated.dll"
test -s "$tmp/OresGenerated.dll"
echo "Guard dual-form structured CIL checks completed; NativeAOT guest execution still requires a separate gate."
