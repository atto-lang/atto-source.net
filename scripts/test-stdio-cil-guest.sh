#!/usr/bin/env bash
# Exact, unmodified Atto guarded return/caret fixtures:
# genuine .atto -> direct CIL -> CoreCLR and NativeAOT with exact stdout.
# The C# bridge only invokes AttoGenerated.Functions.main().
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
printf '2\n2\n' >"$work/expected-stdout"

for entry in \
  return:match-guard-block-return:603df679e1e9b7415cb8b798d4a2143545dd27b8 \
  caret:match-guard-block-caret:0b2addfef29cd76d60d90bf081f4d326ea2d70df; do
  kind="${entry%%:*}"
  rest="${entry#*:}"
  name="${rest%%:*}"
  blob="${rest#*:}"
  source="fixtures/match-return-forms-v1/pass/${name}.atto"
  test "$(git hash-object "$source")" = "$blob" || {
    echo "STDOUT_SOURCE_BLOB_DRIFT $name" >&2; exit 1;
  }
done

dotnet build "$compiler" -c Release

expect_clean_guest() {
  local label="$1" exe="$2"; shift 2
  local status
  set +e
  "$exe" "$@" >"$work/stdout" 2>"$work/stderr"
  status=$?
  set -e
  test "$status" -eq 0 || {
    echo "GUEST_EXIT_MISMATCH $label status=$status" >&2
    cat "$work/stderr" >&2
    exit 1
  }
  cmp "$work/expected-stdout" "$work/stdout" || {
    echo "GUEST_STDOUT_MISMATCH $label" >&2
    od -An -tx1 "$work/stdout" >&2
    exit 1
  }
  test ! -s "$work/stderr" || {
    echo "GUEST_STDERR_NOT_EMPTY $label" >&2
    cat "$work/stderr" >&2
    exit 1
  }
  echo "PASS exact original $label compiled guest stdout=2\\n2\\n exit=0 stderr=empty"
}

for form in return caret; do
  case "$form" in
    return) source=match-guard-block-return ;;
    caret) source=match-guard-block-caret ;;
  esac
  input="fixtures/match-return-forms-v1/pass/${source}.atto"

  # Host must grant stdout deliberately; untrusted default has no process I/O.
  rm -f artifacts/NoGrant.dll
  if dotnet run --no-build -c Release --project "$compiler" -- \
      "$input" artifacts/NoGrant.dll >"$work/compiler.log" 2>"$work/denied"; then
    echo "STDIO_PERMISSION_BYPASS $form" >&2; exit 1
  fi
  grep -Fq "E_PERMISSION:" "$work/denied"
  test ! -e artifacts/NoGrant.dll
  echo "PASS stdout without grant denied $form, no-artifact"

  # The source bytes are unchanged: permission is a host compilation policy.
  dotnet run --no-build -c Release --project "$compiler" -- \
    "$input" "$guest" --allow-stdio
  test -s "$guest"
  dotnet build "$host" -c Release --no-incremental
  expect_clean_guest "CoreCLR-${form}" dotnet run --no-build -c Release --project "$host"

  aot="/tmp/atto-stdio-${form}-nativeaot"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
    --self-contained true -o "$aot"
  test -x "$aot/Atto.Cil.Guest.Host"
  expect_clean_guest "NativeAOT-${form}" "$aot/Atto.Cil.Guest.Host"
done

# Prove incorrect stdio syntax, missing returns, and type violations reject
# before CIL artifact publication even when the trusted stdout flag is set.
for entry in \
  stdio-unknown-member:E_STDIO \
  stdio-empty-arguments:E_STDIO \
  stdio-too-many-arguments:E_STDIO \
  stdio-after-return:E_RETURN \
  stdio-nonint:E_TYPE \
  float-stdio-nonint:E_RETURN_TYPE \
  stdio-missing-return:E_PARSE; do
  name="${entry%%:*}"
  code="${entry#*:}"
  out=artifacts/RejectedStdio.dll
  rm -f "$out"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/match-return-forms-v1/negative/${name}.atto" "$out" --allow-stdio \
    >"$work/rejected.stdout" 2>"$work/rejected.stderr"
  status=$?
  set -e
  test "$status" -eq 2 || {
    echo "STDIO_NEGATIVE_STATUS $name expected=2 actual=$status" >&2; exit 1;
  }
  grep -Fq "$code:" "$work/rejected.stderr" || {
    echo "STDIO_NEGATIVE_DIAGNOSTIC $name expected=$code" >&2
    cat "$work/rejected.stderr" >&2
    exit 1
  }
  test ! -e "$out" || {
    echo "STDIO_NEGATIVE_DLL_LEAK $name" >&2; exit 1;
  }
  echo "PASS stdio-negative $name $code no-artifact"
done
echo "MATCH_STDIO_FULL_ORIGINAL_CORECLR_NATIVEAOT_PASS"
