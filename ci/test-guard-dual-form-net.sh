#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
fixtures=tests/fixtures/guard-dual-form-v1
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT

mode=${1:-coreclr}
case "$mode" in
  coreclr)
    dotnet build "$compiler" -c Release
    command=(dotnet run --project "$compiler" -c Release --no-build --)
    ;;
  macos-arm64)
    test "$(uname -s)" = Darwin
    test "$(uname -m)" = arm64
    dotnet publish "$compiler" -c Release -r osx-arm64 --self-contained true -p:PublishAot=true -o "$tmp/compiler"
    bash verification/assert-macos-native-release.sh "$tmp/compiler/oresnet"
    command=("$tmp/compiler/oresnet")
    ;;
  *) exit 2 ;;
esac
oresnet() { "${command[@]}" "$@"; }

for case in brace-continue:42 keyword-continue:42 keyword-failure:0 short-circuit:42 option-parameter:42 \
            guard-brace-nested-outer-else:42 keyword-nested-outer-else:42 \
            option-result-brace:42 option-result-keyword:42 guard-loop-shift:42; do
  fixture="${case%%:*}"
  expected="${case#*:}"
  output="$tmp/$fixture"
  mkdir -p "$output"
  source="$fixtures/$fixture.atto"
  oresnet check "$source" | grep -F "verified CFG"
  oresnet compile "$source" --out "$output/OresGenerated.dll"
  if [[ "$mode" == coreclr ]]; then
    dotnet build "$host" -c Release --no-incremental -o "$output/app" \
      -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false \
      "-p:GeneratedAssembly=$output/OresGenerated.dll"
    runner=(dotnet "$output/app/oreslang-app.dll")
  else
    dotnet publish "$host" -c Release -r osx-arm64 --self-contained true -p:PublishAot=true -o "$output/app" "-p:GeneratedAssembly=$output/OresGenerated.dll"
    bash verification/assert-macos-native-release.sh "$output/app/oreslang-app"
    runner=(env -i PATH=/usr/bin:/bin "$output/app/oreslang-app")
  fi
  actual="$("${runner[@]}")"
  if [[ "$actual" != "$expected" ]]; then
    printf 'Unexpected guard result for %s (expected %s, got %s)\n' \
      "$fixture" "$expected" "$actual" >&2
    exit 1
  fi
  # Bind execution to the exact CIL and actual launched guest.
  if [[ "$mode" == coreclr ]]; then
    artifact="$output/app/oreslang-app.dll"
  else
    artifact="$output/app/oreslang-app"
  fi
  printf 'GUARD_FORWARD_PORT %s %s value=%s source_blob=%s cil_blob=%s artifact_blob=%s head=%s\n' \
    "$mode" "$fixture" "$actual" "$(git hash-object "$source")" \
    "$(git hash-object "$output/OresGenerated.dll")" "$(git hash-object "$artifact")" "$(git rev-parse HEAD)"
done

for case in reject-fallthrough:E_GUARD reject-mixed:E_GUARD reject-nonbool:E_GUARD \
            "reject-option-argument:Typed helper argument type mismatch" \
            "reject-option-result:Return value does not match function result type" \
            "reject-infix-caret:Reserved AttoLang operator '^'" \
            reject-guard-break:E_GUARD reject-guard-continue:E_GUARD; do
  fixture="${case%%:*}"
  diagnostic="${case#*:}"
  rejected="$tmp/$fixture/OresGenerated.dll"
  mkdir -p "$(dirname "$rejected")"
  for action in check compile; do
    args=("$action" "$fixtures/$fixture.atto")
    if [[ "$action" == compile ]]; then args+=(--out "$rejected"); fi
    status=0
    oresnet "${args[@]}" > "$tmp/stdout" 2> "$tmp/stderr" || status=$?
    if [[ "$status" != 1 || -e "$rejected" ]] ||
        ! grep -Fq "$diagnostic" "$tmp/stderr"; then
      cat "$tmp/stdout" "$tmp/stderr" >&2
      printf 'Unexpected guard rejection for %s %s (exit %s)\n' "$fixture" "$action" "$status" >&2
      exit 1
    fi
  done
  printf 'GUARD_FORWARD_PORT_NO_ARTIFACT %s %s\n' "$mode" "$fixture"
done

printf 'fnc main(): int { return 42; } /* nested /* guard */ comment */\n' > "$tmp/comment.atto"
oresnet check "$tmp/comment.atto" | grep -F "typed OresIR"
if oresnet check "$fixtures/brace-continue.atto" --language-version=2 2>"$tmp/versioned"; then
  echo "Guard routing did not preserve the explicit preview profile" >&2
  exit 1
fi
grep -F GUARD_BACKEND_UNSUPPORTED "$tmp/versioned"
echo "Production guard $mode and negative admission checks passed."
