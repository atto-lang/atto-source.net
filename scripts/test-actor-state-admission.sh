#!/usr/bin/env bash
# Parse-only actor-state contract: no .NET CIL guest may be emitted.
set -euo pipefail
cd "$(dirname "$0")/.."
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
dotnet build "$compiler" -c Release
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

reject() {
  local name="$1" expected="$2" source="$3"
  local output="$tmp/$name.dll"
  rm -f "$output"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$source" "$output" \
    >"$tmp/$name.out" 2>"$tmp/$name.err"
  local code=$?
  set -e
  if [[ "$code" -ne 2 ]]; then
    echo "EXPECTED_COMPILE_REJECTION_FAILED name=$name exit=$code" >&2
    exit 1
  fi
  if ! grep -Fq "$expected:" "$tmp/$name.err"; then
    cat "$tmp/$name.err" >&2
    echo "WRONG_STATE_DIAGNOSTIC name=$name expected=$expected" >&2
    exit 1
  fi
  if [[ -e "$output" ]]; then
    echo "FAILED_CIL_ARTIFACT_LEAK name=$name" >&2
    exit 1
  fi
}

# These are the exact canonical 3 positive target fixtures from conformance
# PR #113. This CIL compiler must FAIL CLOSED until actor guest lowering exists.
reject inline E_LEX fixtures/actor-attached-state-v1/inline_counter.atto
reject attached E_UNSUPPORTED_ACTOR_STATE fixtures/actor-attached-state-v1/attached_counter.atto
reject initialized E_UNSUPPORTED_ACTOR_STATE fixtures/actor-attached-state-v1/attached_initialized.atto

make_source() {
  local name="$1" content="$2"
  printf '%s\n' "$content" >"$tmp/$name.atto"
}
make_source unknown 'define state on actor Unknown as { let int count; }'
reject unknown E_STATE_OWNER "$tmp/unknown.atto"
make_source duplicate 'define actor Counter as end define state on actor Counter as { let int count; } define state on actor Counter as { let int x; }'
reject duplicate E_STATE_DUPLICATE "$tmp/duplicate.atto"
make_source val_uninitialized 'define actor Counter as end define state on actor Counter as { val bool ready; }'
reject val_uninitialized E_STATE_UNINITIALIZED "$tmp/val_uninitialized.atto"
make_source invalid_type 'define actor Counter as end define state on actor Counter as { let string count; }'
reject invalid_type E_STATE_TYPE "$tmp/invalid_type.atto"
make_source invalid_literal 'define actor Counter as end define state on actor Counter as { let int count: 9223372036854775808; }'
reject invalid_literal E_STATE_LITERAL "$tmp/invalid_literal.atto"
make_source wrong_owner 'define class Counter as end define state on actor Counter as { let int count; }'
reject wrong_owner E_STATE_OWNER "$tmp/wrong_owner.atto"
make_source comment_spoof 'pub fnc main(): int { return 42; } // define state on actor Counter as { let int count; }'
dotnet run --no-build -c Release --project "$compiler" -- "$tmp/comment_spoof.atto" "$tmp/comment_spoof.dll" \
  >"$tmp/comment_spoof.out" 2>"$tmp/comment_spoof.err"
test -s "$tmp/comment_spoof.dll"
echo 'ATTO_NET_ATTACHED_ACTOR_STATE_PARSE_ONLY_PASS checks=10 compiled_guest_state=0'
