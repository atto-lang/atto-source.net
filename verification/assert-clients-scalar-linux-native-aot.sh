#!/usr/bin/env bash
# Same byte-identical Oreslang scalar sources used by the public clients pilot.
# Six compiler checks + three compiler denials + two real Native AOT executions.
# Does not establish module, JSON, async, transport or full SDK parity.
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$root"
compiler="$root/artifacts/compiler/linux-x64/oresnet"
[[ -f "$compiler" && -x "$compiler" && ! -L "$compiler" ]] || {
  echo 'Missing built Native AOT compiler' >&2
  exit 2
}
bash verification/assert-linux-native-release.sh "$compiler"
src='contracts/clients/scalar-v1'
sha256sum --check --status <<'CHECKSUMS'
e42464360ffa360b263bc74f74ef485e6a9e4eb9dff601ead2c4576982622576  contracts/clients/scalar-v1/01-precedence.ores
9196a61de9c4710bdf32e658de396c521ad2abb2e3d03ce711ae449ac7aa5a2f  contracts/clients/scalar-v1/02-grouping.ores
613d1e1e104361ce555f397dd0d23754d44ade5b80750147110c1e4a73cce286  contracts/clients/scalar-v1/03-signed.ores
b0f80c76a24cb15ed25fee0bdaa096d7cbcd318f787f7d8a040166c127532247  contracts/clients/scalar-v1/04-i64-edge.ores
2bcfdc119d3e48b9ac97e2061b7b88571d6e621b04ecabf886347d9eeb19382c  contracts/clients/scalar-v1/05-separators.ores
dd1bf333cdd17140571227e810427030a279666b8a4d06eb5968a056cf5a8a2a  contracts/clients/scalar-v1/06-nested.ores
c45493f0814253ab5a2e8896d8b39643c7b8e1247d408eef11baccd79d6580e1  contracts/clients/scalar-v1/07-unknown-reject.ores
37b793a36e231ee37d953b8359cd0f004dc008a795eb32a413762a0a504c542c  contracts/clients/scalar-v1/08-overflow-reject.ores
32f4b30c52ce58955ecbb0aa0d564f0ac394737940faaab01351e25ed9df4b75  contracts/clients/scalar-v1/09-syntax-reject.ores
CHECKSUMS

for name in 01-precedence 02-grouping 03-signed 04-i64-edge 05-separators 06-nested; do
  "$compiler" check "$src/$name.ores"
done

# A negative must reject at compile time without leaving any generated CIL.
# Use distinct outputs and refuse stale artifacts masking a failure.
for name in 07-unknown-reject 08-overflow-reject 09-syntax-reject; do
  output="artifacts/client-scalar-rejected/$name/OresGenerated.dll"
  mkdir -p -- "$(dirname -- "$output")"
  rm -f -- "$output"
  if "$compiler" compile "$src/$name.ores" --out "$output" >"artifacts/client-scalar-rejected/$name/diagnostic.txt" 2>&1; then
    echo "Rejected client scalar unexpectedly compiled: $name" >&2
    exit 1
  fi
  test ! -e "$output" || { echo "Rejected client scalar published CIL: $name" >&2; exit 1; }
  test -s "artifacts/client-scalar-rejected/$name/diagnostic.txt" || {
    echo "Rejected client scalar lacked a diagnostic: $name" >&2
    exit 1
  }
done

# Native AOT pipeline, never a managed dotnet run fallback.
for pair in '01-precedence:14' '02-grouping:20'; do
  name="${pair%%:*}"
  expected="${pair#*:}"
  # The existing source compiler + static native linker is the only release path.
  bash scripts/publish-linux.sh "$src/$name.ores" "artifacts/client-scalar-$name"
  exe="artifacts/client-scalar-$name/linux-x64/oreslang-app"
  bash verification/assert-linux-native-release.sh "$exe"
  actual="$("$exe")"
  if [[ "$actual" != "$expected" ]]; then
    echo "Native AOT scalar result mismatch: $name" >&2
    exit 1
  fi
done
echo 'NET_CLIENT_SCALAR_PARTIAL_6_CHECKS_3_NEGATIVES_2_NATIVE_AOT_EXECUTIONS'
