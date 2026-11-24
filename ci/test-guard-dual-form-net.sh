#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
fixtures=tests/fixtures/guard-dual-form-v1
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT

dotnet build "$compiler" -c Release
oresnet() {
  dotnet run --project "$compiler" -c Release --no-build -- "$@"
}

for case in brace-continue:42 keyword-continue:42 keyword-failure:0 short-circuit:42 option-parameter:42 \
            guard-brace-nested-outer-else:42 keyword-nested-outer-else:42 \
            option-result-brace:42 option-result-keyword:42; do
  fixture="${case%%:*}"
  expected="${case#*:}"
  output="$tmp/$fixture"
  mkdir -p "$output"
  source="$fixtures/$fixture.atto"
  oresnet check "$source" | grep -F "verified CFG"
  oresnet compile "$source" --out "$output/OresGenerated.dll"
  dotnet build "$host" -c Release --no-incremental -o "$output/app" \
    -p:PublishAot=false -p:SelfContained=false -p:PublishTrimmed=false \
    "-p:GeneratedAssembly=$output/OresGenerated.dll"
  actual="$(dotnet "$output/app/oreslang-app.dll")"
  if [[ "$actual" != "$expected" ]]; then
    printf 'Unexpected guard result for %s (expected %s, got %s)\n' \
      "$fixture" "$expected" "$actual" >&2
    exit 1
  fi
  printf 'CoreCLR production guard %s = %s\n' "$fixture" "$actual"
done

for case in reject-fallthrough:E_GUARD reject-mixed:E_GUARD reject-nonbool:E_GUARD \
            "reject-option-argument:Typed helper argument type mismatch" \
            "reject-option-result:Return value does not match function result type" \
            "reject-infix-caret:Reserved AttoLang operator '^'"; do
  fixture="${case%%:*}"
  diagnostic="${case#*:}"
  rejected="$tmp/OresGenerated.dll"
  status=0
  oresnet compile "$fixtures/$fixture.atto" --out "$rejected" > "$tmp/stdout" 2> "$tmp/stderr" || status=$?
  if [[ "$status" != 1 || -e "$rejected" ]] ||
      ! grep -Fq "$diagnostic" "$tmp/stderr"; then
    cat "$tmp/stderr" >&2
    printf 'Unexpected guard rejection for %s (exit %s)\n' "$fixture" "$status" >&2
    exit 1
  fi
done

printf 'fnc main(): int { return 42; } /* nested /* guard */ comment */\n' > "$tmp/comment.atto"
oresnet check "$tmp/comment.atto" | grep -F "typed OresIR"
if oresnet check "$fixtures/brace-continue.atto" --language-version=2 2>"$tmp/versioned"; then
  echo "Guard routing did not preserve the explicit preview profile" >&2
  exit 1
fi
grep -F GUARD_BACKEND_UNSUPPORTED "$tmp/versioned"
echo "Production guard CoreCLR and negative admission checks passed."
