#!/usr/bin/env bash
set -Eeuo pipefail
export LC_ALL=C
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
project=verification/Ores.Net.AttachedStructFixtures/Ores.Net.AttachedStructFixtures.csproj
fixtures=verification/Ores.Net.AttachedStructFixtures
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
dotnet build "$project" -c Release
compile(){ dotnet run --no-build --project "$project" -c Release -- "$1" "$2"; }
positive=0
negative=0
for fixture in nested:42 mutable:1316 ordered:1336; do
  name=${fixture%:*}
  expected=${fixture#*:}
  source="$fixtures/$name.atto"
  case_dir="$work/positive-$name"
  mkdir "$case_dir"
  compile "$source" "$case_dir/OresGenerated.dll"
  compile "$source" "$case_dir/repro.dll"
  test -s "$case_dir/OresGenerated.dll"
  test -s "$case_dir/repro.dll"
  cmp "$case_dir/OresGenerated.dll" "$case_dir/repro.dll"
  dotnet clean "$host" -c Release -r linux-x64 "-p:GeneratedAssembly=$case_dir/OresGenerated.dll"
  dotnet publish "$host" -c Release -r linux-x64 -o "$case_dir/published" "-p:GeneratedAssembly=$case_dir/OresGenerated.dll" -warnaserror
  app="$case_dir/published/oreslang-app"
  test -x "$app"
  file "$app" | grep -q 'ELF 64-bit'
  for sidecar in OresGenerated.dll oreslang-app.runtimeconfig.json oreslang-app.deps.json; do
    test ! -e "$case_dir/published/$sidecar"
    test ! -L "$case_dir/published/$sidecar"
  done
  env -i PATH=/usr/bin:/bin "$app" >"$case_dir/stdout" 2>"$case_dir/stderr"
  printf '%s\n' "$expected" >"$case_dir/expected"
  cmp "$case_dir/expected" "$case_dir/stdout"
  test ! -s "$case_dir/stderr"
  sha256sum "$source" "$case_dir/OresGenerated.dll" "$app"
  echo "NET_ATTACHED_STRUCT_NATIVE_AOT_PASS $source result=$expected"
  positive=$((positive+1))
done

# Pin the primary diagnostic for each original fixture, not an arbitrary
# code occurring anywhere in a combined stdout/stderr log.
declare -A codes=(
  [cycle]=E_TYPE [duplicate-init]=E_STRUCT_INIT
  [immutable-receiver]=E_BORROW_MUT_REQUIRED [invalid-projection]=E_TYPE
  [missing-init]=E_STRUCT_INIT [mutable-unsupported]=E_STRUCT_SYNTAX
  [mutation-as-expression]=E_STRUCT_SYNTAX [readonly-write]=E_STRUCT_SYNTAX
  [trailing]=E_STRUCT_SYNTAX [wrong-constructor]=E_TYPE
  [wrong-identity]=E_TYPE [wrong-mutation-arg]=E_TYPE [wrong-mutation-field]=E_TYPE
)
shopt -s nullglob
inputs=("$fixtures"/negative/*.atto)
test "${#inputs[@]}" = "${#codes[@]}"
for input in "${inputs[@]}"; do
  name="$(basename "$input" .atto)"
  expected="${codes[$name]:?unexpected negative fixture}"
  case_dir="$work/negative-$name"
  mkdir "$case_dir"
  set +e
  compile "$input" "$case_dir/denied.dll" >"$case_dir/stdout" 2>"$case_dir/stderr"
  status=$?
  set -e
  test "$status" = 1
  test ! -s "$case_dir/stdout"
  test ! -e "$case_dir/denied.dll"
  test ! -L "$case_dir/denied.dll"
  # Exactly one nonempty diagnostic line from the fixture driver's catch.
  mapfile -t diagnostics <"$case_dir/stderr"
  test "${#diagnostics[@]}" = 1
  if [[ "${diagnostics[0]}" != "$expected: "?* ]]; then
    printf 'Expected %s for %s, observed: %s\n' "$expected" "$name" "${diagnostics[0]}" >&2
    exit 1
  fi
  echo "NET_ATTACHED_STRUCT_REJECT $name $expected"
  negative=$((negative+1))
done
test "$positive" = 3
test "$negative" = 13
echo "NET_ATTACHED_STRUCT_SUMMARY positive=$positive negative=$negative failures=0"
