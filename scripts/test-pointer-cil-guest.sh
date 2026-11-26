#!/usr/bin/env bash
# Real Atto .atto -> direct ECMA-335 CIL -> managed CLR and NativeAOT.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

expect_42() {
  local label="$1"; shift
  local result
  set +e
  "$@"
  result=$?
  set -e
  if [[ "$result" -ne 42 ]]; then
    echo "EXPECTED_EXIT_42_FAILED label=$label actual=$result" >&2
    exit 1
  fi
  echo "ATTO_DIRECT_CIL_$label"_PASS exit=42
}
dotnet build "$compiler" -c Release
# Compiled guest parity for the bounded non-owning local Pointer<int> profile.
# Each source fixture is compiled to CIL, then executed in CoreCLR AND
# in a separately published NativeAOT executable. The C# bridge is fixed;
# the return value originates exclusively in compiled .atto guest code.
for fixture in \
  fixtures/cil/pointer-local-rt.atto \
  fixtures/cil/pointer-local-mixed-bang.atto \
  fixtures/cil/pointer-local-all-bang.atto; do
  name="$(basename "$fixture" .atto)"
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  expect_42 POINTER_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
  output="$work/native-$name"
  dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
    --self-contained true -o "$output"
  test -x "$output/Atto.Cil.Guest.Host"
  expect_42 POINTER_NATIVEAOT_GUEST "$output/Atto.Cil.Guest.Host"
done
echo "ATTO_POINTER_LOCAL_CIL_NATIVEAOT_PASS guests=3"

# All pointer-negative fixtures require a scoped E_POINTER_* failure and
# an empty emitted-artifact directory. This is compiled-source rejection,
# NOT a regex-only lint or a host-model-only witness.
pointer_reject_count=0
for fixture in fixtures/cil/negative/pointer-*.atto; do
  diagnostic="$(sed -n 's|^// expect-error: ||p' "$fixture")"
  test -n "$diagnostic" || { echo "MISSING_POINTER_DIAGNOSTIC $fixture" >&2; exit 1; }
  rm -f artifacts/Rejected.dll
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "$fixture" artifacts/Rejected.dll \
    >/tmp/atto-pointer-negative.stdout 2>/tmp/atto-pointer-negative.stderr
  result=$?
  set -e
  test "$result" -eq 2 || { echo "POINTER_NEGATIVE_STATUS_FAILED $fixture rc=$result" >&2; exit 1; }
  grep -Fq "$diagnostic:" /tmp/atto-pointer-negative.stderr || {
    cat /tmp/atto-pointer-negative.stderr
    echo "POINTER_NEGATIVE_DIAGNOSTIC_FAILED $fixture expected=$diagnostic" >&2
    exit 1
  }
  test ! -e artifacts/Rejected.dll || {
    echo "POINTER_NEGATIVE_CIL_ARTIFACT_LEAK $fixture" >&2
    exit 1
  }
  pointer_reject_count=$((pointer_reject_count + 1))
done
test "$pointer_reject_count" -eq 19
echo "ATTO_POINTER_LOCAL_CIL_NEGATIVE_PASS rejected=$pointer_reject_count"

# Additional fail-closed lexical and authority cases; comments are not tokens.
printf 'pub fnc main(): int { return 42; } // Pointer ref!\n' >"$work/comment.atto"
rm -f "$guest"
dotnet run --no-build -c Release --project "$compiler" -- "$work/comment.atto" "$guest"
dotnet build "$host" -c Release -t:Rebuild
expect_42 POINTER_COMMENT_CORECLR dotnet run --no-build -c Release --project "$host"
for name in await borrow ref; do
  printf 'pub fnc main(): int { let %s: int = 42; val Pointer<int> ptr = rt ref %s; val answer = rt deref ptr; rt unref ptr; return answer; }\n' "$name" "$name" >"$work/reserved.atto"
  rm -f "$work/rejected.dll"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$work/reserved.atto" "$work/rejected.dll" >"$work/out" 2>"$work/err"
  rc=$?
  set -e
  test "$rc" -eq 2 && grep -Fq E_POINTER_NAME: "$work/err" && test ! -e "$work/rejected.dll"
done
echo ATTO_POINTER_EXTRA_ADMISSION_PASS
