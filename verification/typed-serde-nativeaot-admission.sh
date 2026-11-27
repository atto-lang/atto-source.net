#!/usr/bin/env bash
# Native AOT compiler/guest scalar control and codec admission only.
set -euo pipefail
root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$root"
bash scripts/publish-linux.sh contracts/atto-parity-v1/add.atto artifacts/typed-serde-admission
compiler="$root/artifacts/compiler/linux-x64/oresnet"
guest="$root/artifacts/typed-serde-admission/linux-x64/oreslang-app"
test "$("$guest")" = 42
dir="$(mktemp -d)"
trap 'rm -rf -- "$dir"' EXIT
reject() {
  local label="$1" source="$2" rc=0
  printf '%s\n' "$source" > "$dir/$label.atto"
  rm -f "$dir/OresGenerated.dll"
  "$compiler" compile "$dir/$label.atto" --out "$dir/OresGenerated.dll" > "$dir/stdout" 2> "$dir/stderr" || rc=$?
  test "$rc" = 1
  test ! -e "$dir/OresGenerated.dll"
  test ! -s "$dir/stdout"
  grep -Eq '^oresnet: .+' "$dir/stderr"
  # Explicitly exclude infrastructure and IO failures; accept the current
  # restricted scalar compiler's source-level unsupported/syntax diagnostic.
  grep -Eiq 'unsupported|expected|unexpected|unknown|undefined|unbound|identifier|declaration|nominal' "$dir/stderr"
  cat "$dir/stderr"
  echo "SERDE_ADMISSION_DENIED $label"
}
reject missing 'pub fnc main(): int { return deserialize; }'
reject class 'define class User as pub to_json(): string { return "x"; } end pub fnc main(): int { return 0; }'
reject struct 'define struct User implements Serializable as value: int; end pub fnc main(): int { return 0; }'
reject interface 'define interface JsonSerializable as to_json(): string; end pub fnc main(): int { return 0; }'
reject trait 'define trait JsonDeserializable as from_json(string input): string; end pub fnc main(): int { return 0; }'
echo 'NET_TYPED_SERDE_ADMISSION_PASS checks=6 NOT_SERIALIZER_EXECUTION'
