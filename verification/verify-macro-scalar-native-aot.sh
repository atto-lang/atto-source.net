#!/usr/bin/env bash
# Source-owned end-to-end macro gate. Run on an approved source-bound Linux
# runner with .NET SDK 10 and Native AOT toolchain installed.
# Does NOT mirror private source or use foreign credentials.
set -Eeuo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

for tool in dotnet git file readelf realpath cmp sha256sum timeout; do
  command -v "$tool" >/dev/null || { echo "MACRO_AOT_NOT_EXECUTED missing $tool" >&2; exit 2; }
done
actual_head="$(git rev-parse HEAD)"
if [[ -n "${MACRO_EXPECTED_HEAD:-}" && "$actual_head" != "$MACRO_EXPECTED_HEAD" ]]; then
  echo "MACRO_AOT_NOT_EXECUTED exact-head mismatch expected=$MACRO_EXPECTED_HEAD actual=$actual_head" >&2
  exit 2
fi
if ! git diff --quiet -- . || ! git diff --cached --quiet -- .; then
  echo "MACRO_AOT_NOT_EXECUTED dirty tracked source before tests" >&2
  exit 2
fi
printf 'MACRO_AOT_SOURCE_HEAD=%s\n' "$actual_head"
printf 'MACRO_AOT_SOURCE_TREE=%s\n' "$(git rev-parse HEAD^{tree})"
printf 'MACRO_AOT_DOTNET_SDK=%s\n' "$(dotnet --version)"

test_project="verification/Ores.Net.Tests/Ores.Net.Tests.csproj"
dotnet run --project "$test_project" -c Release
echo "MACRO_AOT_HOST_TESTS_PASS"

source_root="$root/verification/fixtures"
declare -a positive=(macro-literal-42.atto macro-identity-42.atto macro-nested-42.atto
    macro-template-twice-42.atto macro-template-twice-nested-42.atto)
declare -a negative=(macro-overflow-reject.atto macro-public-reject.atto macro-wrong-kind-reject.atto
    macro-runtime-const-reject.atto macro-unresolved-reject.atto
    macro-template-twice-name-reject.atto macro-template-twice-composite-reject.atto
    macro-template-twice-overflow-reject.atto)

tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT

# The first guest publish builds the compiler itself with Native AOT.
# Each app is independently linked as strict Native AOT from checked CIL.
positive_passes=0
for name in "${positive[@]}"; do
  source="$source_root/$name"
  case "$name" in
    macro-literal-42.atto) dest=artifacts/macro-literal ;;
    macro-identity-42.atto) dest=artifacts/macro-identity ;;
    macro-nested-42.atto) dest=artifacts/macro-nested ;;
    macro-template-twice-42.atto) dest=artifacts/macro-template-twice ;;
    macro-template-twice-nested-42.atto) dest=artifacts/macro-template-twice-nested ;;
    *) echo "unexpected macro fixture" >&2; exit 2 ;;
  esac
  bash scripts/publish-linux.sh "$source" "$dest"
  guest="$root/$dest/linux-x64/oreslang-app"
  bash verification/assert-linux-native-release.sh "$guest"
  # RuntimeHost.ExecuteAsync writes the guest's i64 value to stdout and
  # returns status 0 on success. Exit code 42 is NOT its value ABI.
  set +e
  timeout --signal=TERM --kill-after=2s 10s "$guest" >"$tmp/guest.stdout" 2>"$tmp/guest.stderr"
  actual=$?
  set -e
  if [[ $actual -ne 0 ]] || ! cmp -s -- "$tmp/guest.stdout" <(printf '42\n') || \
     [[ -s "$tmp/guest.stderr" ]]; then
    echo "MACRO_AOT_GUEST_FAIL fixture=$name expected_stdout=42 expected_exit=0 actual_exit=$actual" >&2
    cat "$tmp/guest.stdout" >&2
    cat "$tmp/guest.stderr" >&2
    exit 1
  fi
  # Bind each successful receipt to the exact source bytes, native
  # compiler, verified/generated CIL and final AOT guest. The checkout
  # commit/tree are recorded above for the whole run.
  printf 'MACRO_AOT_GUEST_PASS fixture=%s stdout=42 exit=0 source_sha256=%s compiler_sha256=%s cil_sha256=%s guest_sha256=%s\n' \
    "$name" "$(sha256sum "$source" | cut -d' ' -f1)" \
    "$(sha256sum "$root/artifacts/compiler/linux-x64/oresnet" | cut -d' ' -f1)" \
    "$(sha256sum "$root/artifacts/generated/linux-x64/OresGenerated.dll" | cut -d' ' -f1)" \
    "$(sha256sum "$guest" | cut -d' ' -f1)"
  positive_passes=$((positive_passes + 1))
done

compiler="$root/artifacts/compiler/linux-x64/oresnet"
bash verification/assert-linux-native-release.sh "$compiler"
generated_cil="$root/artifacts/generated/linux-x64/OresGenerated.dll"
[[ -s "$generated_cil" ]] || { echo "MACRO_AOT_NOT_EXECUTED missing final CIL" >&2; exit 2; }
baseline_cil_sha="$(sha256sum "$generated_cil" | cut -d' ' -f1)"
baseline_compiler_sha="$(sha256sum "$compiler" | cut -d' ' -f1)"
negative_work="$tmp/rejection-work"
negative_passes=0

# Failure must happen before artifact publication, on the real released
# Native AOT compiler binary, for check/compile/source-emission commands.
for name in "${negative[@]}"; do
  source="$source_root/$name"
  case "$name" in
    macro-overflow-reject.atto|macro-template-twice-overflow-reject.atto)
      needle=NET_MACRO_CONST_OVERFLOW ;;
    macro-public-reject.atto|macro-wrong-kind-reject.atto|macro-unresolved-reject.atto|macro-template-twice-name-reject.atto|macro-template-twice-composite-reject.atto)
      needle=NET_MACRO_EXPANDER_REQUIRED ;;
    macro-runtime-const-reject.atto) needle="Expected integer" ;;
    *) exit 2 ;;
  esac
  for command in check compile emit-csharp emit-fsharp; do
    rm -rf -- "$negative_work"
    mkdir -p -- "$negative_work"
    rm -f -- "$tmp/OresGenerated.dll" "$tmp/Source.cs" "$tmp/Source.fs" "$tmp/stdout" "$tmp/stderr"
    case "$command" in
      check) invocation=("$compiler" check "$source") ;;
      compile) invocation=("$compiler" compile "$source" --out "$tmp/OresGenerated.dll") ;;
      emit-csharp) invocation=("$compiler" emit-csharp "$source" --out "$tmp/Source.cs") ;;
      emit-fsharp) invocation=("$compiler" emit-fsharp "$source" --out "$tmp/Source.fs") ;;
    esac
    set +e
    ( cd -- "$negative_work"; timeout --signal=TERM --kill-after=2s 20s "${invocation[@]}" ) >"$tmp/stdout" 2>"$tmp/stderr"
    status=$?
    set -e
    # The production CLI returns exactly 1 for a typed FormatException.
    # Usage errors, runtime crashes, timeouts and missing executables are
    # infrastructure/code failures, never successful fail-closed rejections.
    if [[ $status -ne 1 ]]; then
      echo "MACRO_AOT_REJECTION_FAILED command=$command fixture=$name unexpected_status=$status" >&2
      exit 1
    fi
    if ! grep -Fq -- "$needle" "$tmp/stderr"; then
      echo "MACRO_AOT_REJECTION_FAILED command=$command fixture=$name diagnostic=$needle" >&2
      cat "$tmp/stderr" >&2
      exit 1
    fi
    if [[ -s "$tmp/stdout" ]] || [[ -e "$tmp/OresGenerated.dll" ]] || \
       [[ -e "$tmp/Source.cs" ]] || [[ -e "$tmp/Source.fs" ]] || \
       [[ -n "$(find "$negative_work" -mindepth 1 -print -quit)" ]]; then
      echo "MACRO_AOT_LEAKED_OUTPUT command=$command fixture=$name" >&2
      exit 1
    fi
    if [[ "$(sha256sum "$generated_cil" | cut -d' ' -f1)" != "$baseline_cil_sha" ]] || \
       [[ "$(sha256sum "$compiler" | cut -d' ' -f1)" != "$baseline_compiler_sha" ]]; then
      echo "MACRO_AOT_MUTATED_RELEASE command=$command fixture=$name" >&2
      exit 1
    fi
    negative_passes=$((negative_passes + 1))
    printf 'MACRO_AOT_REJECTION_PASS command=%s fixture=%s\n' "$command" "$name"
  done
done

if [[ $positive_passes -ne 5 || $negative_passes -ne 32 ]]; then
  echo "MACRO_AOT_INCOMPLETE expected_positives=5 actual=$positive_passes expected_rejections=32 actual=$negative_passes" >&2
  exit 1
fi
if ! git diff --quiet -- . || ! git diff --cached --quiet -- .; then
  echo "MACRO_AOT_MUTATED_TRACKED_SOURCE" >&2
  exit 1
fi
printf 'MACRO_AOT_COUNTS guests=%s rejections=%s\n' "$positive_passes" "$negative_passes"
echo "MACRO_AOT_EXACT_SOURCE_FULL_GATE_PASS"
