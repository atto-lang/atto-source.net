#!/usr/bin/env bash
# Actual NativeAOT compiler + separately statically linked guest. No CoreCLR
# substitution. Source-local fixtures are generated deterministically.
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
head="$(git rev-parse HEAD)"
tree="$(git rev-parse HEAD^{tree})"
[[ -z "$(git status --porcelain --untracked-files=all)" ]]
printf 'SOURCE_HEAD=%s SOURCE_TREE=%s SDK=%s\n' "$head" "$tree" "$(dotnet --version)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
rid=linux-x64
compiler_dir="$work/compiler"
compiler="$compiler_dir/oresnet"
host="$root/host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj"
dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r "$rid" -o "$compiler_dir" -warnaserror
bash verification/assert-linux-native-release.sh "$compiler"
pre='define dimension Time; define unit Second: Time; define unit Millis: Time = Second / 1000; type Positive = int where self > 0 and units in Second;'
cases=0
guest() {
  local name="$1" expr="$2" expected_status="$3" expected_text="$4" source="$work/$1.atto" generated="$work/$1/OresGenerated.dll" output="$work/$1/native"
  mkdir -p -- "$work/$name"
  printf '%s\npub fnc main() -> int { return %s; }\n' "$pre" "$expr" >"$source"
  "$compiler" compile "$source" --out "$generated"
  [[ -s "$generated" ]]
  dotnet clean "$host" -c Release -r "$rid" "-p:GeneratedAssembly=$generated"
  dotnet publish "$host" -c Release -r "$rid" -o "$output" "-p:GeneratedAssembly=$generated" -warnaserror
  bash verification/assert-linux-native-release.sh "$output/oreslang-app"
  local status=0
  timeout --signal=TERM --kill-after=2s 10s "$output/oreslang-app" >"$work/stdout" 2>"$work/stderr" || status=$?
  [[ "$status" -eq "$expected_status" ]]
  if [[ "$expected_status" -eq 0 ]]; then
    cmp -s "$work/stdout" <(printf '%s\n' "$expected_text")
    [[ ! -s "$work/stderr" ]]
  else
    [[ ! -s "$work/stdout" ]]
    cmp -s "$work/stderr" <(printf '%s\n' "$expected_text")
  fi
  printf 'NATIVE_AOT_PASS %s exit=%s\n' "$name" "$status"
  sha256sum -- "$source" "$compiler" "$generated" "$output/oreslang-app"
  cases=$((cases+1))
}
guest prefix '(<Positive>(42.<Second>)) / 1<Second>' 0 42
guest postfix '(42.<Second>::<Positive>) / 1<Second>' 0 42
guest keyword '(42.<Second> as Positive) / 1<Second>' 0 42
guest invalid-prefix '(<Positive>(0.<Second>)) / 1<Second>' 70 'Oreslang refinement check failed'
guest invalid-postfix '(0.<Second>::<Positive>) / 1<Second>' 70 'Oreslang refinement check failed'
guest invalid-keyword '(0.<Second> as Positive) / 1<Second>' 70 'Oreslang refinement check failed'
guest zero '1.<Second> / 0.<Second>' 70 'Oreslang division by zero'
reject() {
  local name="$1" expr="$2" diagnostic="$3" source="$work/$1.atto" status=0
  printf '%s\npub fnc main() -> int { return %s; }\n' "$pre" "$expr" >"$source"
  "$compiler" compile "$source" --out "$work/$name.dll" >"$work/stdout" 2>"$work/stderr" || status=$?
  [[ "$status" -eq 1 && ! -e "$work/$name.dll" && ! -s "$work/stdout" ]]
  grep -Fq "$diagnostic:" "$work/stderr"
  printf 'NATIVE_AOT_REJECT %s %s\n' "$name" "$diagnostic"
  cases=$((cases+1))
}
reject erase '42.<Second>::<i64>' E_UNIT_ERASURE
reject retag '42.<Second>.<Second>' E_UNIT_ATTACHMENT
reject scale '(42<Millis> as Positive) / 1<Second>' E_UNIT_SCALE
[[ "$cases" -eq 10 ]]
git diff --quiet -- . && git diff --cached --quiet -- .
printf 'NATIVE_AOT_VERIFIED %s cases\n' "$cases"
