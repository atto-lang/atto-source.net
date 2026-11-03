#!/usr/bin/env bash
# Source-owned end-to-end macro gate. Run on an approved source-bound Linux
# runner with .NET SDK 10 and Native AOT toolchain installed.
# Does NOT mirror private source or use foreign credentials.
set -Eeuo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

for tool in dotnet git file readelf realpath cmp sha256sum; do
  command -v "$tool" >/dev/null || { echo "MACRO_AOT_NOT_EXECUTED missing $tool" >&2; exit 2; }
done
printf 'MACRO_AOT_SOURCE_HEAD=%s\n' "$(git rev-parse HEAD)"
printf 'MACRO_AOT_SOURCE_TREE=%s\n' "$(git rev-parse HEAD^{tree})"
printf 'MACRO_AOT_DOTNET_SDK=%s\n' "$(dotnet --version)"

test_project="verification/Ores.Net.Tests/Ores.Net.Tests.csproj"
dotnet run --project "$test_project" -c Release
echo "MACRO_AOT_HOST_TESTS_PASS"

source_root="$root/verification/fixtures"
declare -a positive=(macro-literal-42.atto macro-identity-42.atto macro-nested-42.atto)
declare -a negative=(macro-overflow-reject.atto macro-public-reject.atto macro-wrong-kind-reject.atto macro-runtime-const-reject.atto)

tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT

# The first guest publish builds the compiler itself with Native AOT.
# Each app is independently linked as strict Native AOT from checked CIL.
for name in "${positive[@]}"; do
  source="$source_root/$name"
  case "$name" in
    macro-literal-42.atto) dest=artifacts/macro-literal ;;
    macro-identity-42.atto) dest=artifacts/macro-identity ;;
    macro-nested-42.atto) dest=artifacts/macro-nested ;;
    *) echo "unexpected macro fixture" >&2; exit 2 ;;
  esac
  bash scripts/publish-linux.sh "$source" "$dest"
  guest="$root/$dest/linux-x64/oreslang-app"
  bash verification/assert-linux-native-release.sh "$guest"
  # RuntimeHost.ExecuteAsync writes the guest's i64 value to stdout and
  # returns status 0 on success. Exit code 42 is NOT its value ABI.
  set +e
  "$guest" >"$tmp/guest.stdout" 2>"$tmp/guest.stderr"
  actual=$?
  set -e
  if [[ $actual -ne 0 ]] || ! cmp -s -- "$tmp/guest.stdout" <(printf '42\n') || \
     [[ -s "$tmp/guest.stderr" ]]; then
    echo "MACRO_AOT_GUEST_FAIL fixture=$name expected_stdout=42 expected_exit=0 actual_exit=$actual" >&2
    cat "$tmp/guest.stdout" >&2
    cat "$tmp/guest.stderr" >&2
    exit 1
  fi
  printf 'MACRO_AOT_GUEST_PASS fixture=%s stdout=42 exit=0 sha256=%s\n' \
    "$name" "$(sha256sum "$guest" | cut -d' ' -f1)"
done

compiler="$root/artifacts/compiler/linux-x64/oresnet"
bash verification/assert-linux-native-release.sh "$compiler"

# Failure must happen before artifact publication, on the real released
# Native AOT compiler binary, for check/compile/source-emission commands.
for name in "${negative[@]}"; do
  source="$source_root/$name"
  case "$name" in
    macro-overflow-reject.atto) needle=NET_MACRO_CONST_OVERFLOW ;;
    macro-public-reject.atto|macro-wrong-kind-reject.atto)
      needle=NET_MACRO_EXPANDER_REQUIRED ;;
    macro-runtime-const-reject.atto) needle="Expected integer" ;;
    *) exit 2 ;;
  esac
  for command in check compile emit-csharp emit-fsharp; do
    rm -f -- "$tmp/OresGenerated.dll" "$tmp/Source.cs" "$tmp/Source.fs" "$tmp/stdout" "$tmp/stderr"
    case "$command" in
      check) invocation=("$compiler" check "$source") ;;
      compile) invocation=("$compiler" compile "$source" --out "$tmp/OresGenerated.dll") ;;
      emit-csharp) invocation=("$compiler" emit-csharp "$source" --out "$tmp/Source.cs") ;;
      emit-fsharp) invocation=("$compiler" emit-fsharp "$source" --out "$tmp/Source.fs") ;;
    esac
    if "${invocation[@]}" >"$tmp/stdout" 2>"$tmp/stderr"; then
      echo "MACRO_AOT_REJECTION_FAILED command=$command fixture=$name (unexpected success)" >&2
      exit 1
    fi
    if ! grep -Fq -- "$needle" "$tmp/stderr"; then
      echo "MACRO_AOT_REJECTION_FAILED command=$command fixture=$name diagnostic=$needle" >&2
      cat "$tmp/stderr" >&2
      exit 1
    fi
    if [[ -s "$tmp/stdout" ]] || [[ -e "$tmp/OresGenerated.dll" ]] || \
       [[ -e "$tmp/Source.cs" ]] || [[ -e "$tmp/Source.fs" ]]; then
      echo "MACRO_AOT_LEAKED_OUTPUT command=$command fixture=$name" >&2
      exit 1
    fi
    printf 'MACRO_AOT_REJECTION_PASS command=%s fixture=%s\n' "$command" "$name"
  done
done

echo "MACRO_AOT_EXACT_SOURCE_FULL_GATE_PASS"
