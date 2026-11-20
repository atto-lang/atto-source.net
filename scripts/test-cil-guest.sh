#!/usr/bin/env bash
# Real Atto .atto -> direct ECMA-335 CIL -> managed CLR and NativeAOT.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts

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
dotnet run --no-build -c Release --project "$compiler" -- fixtures/cil/exit42.atto "$guest"
test -s "$guest"

for case in \
  duplicate:E_DUP_FUNCTION \
  undefined:E_SYMBOL \
  arity:E_ARITY \
  cycle:E_CYCLE \
  unsupported-actor:E_PARSE \
  main-arity:E_ENTRY \
  overflow:E_LITERAL \
  unknown-parameter:E_SYMBOL \
  no-return:E_PARSE \
  duplicate-parameter:E_DUP_PARAM \
  bool-unsupported:E_PARSE \
  pipe-unsupported:E_UNSUPPORTED_CONTINUOUS_PIPE \
  rpipe-unsupported:E_UNSUPPORTED_CONTINUOUS_PIPE \
  no-main:E_ENTRY; do
  name="$(echo "$case" | cut -d: -f1)"
  diagnostic="$(echo "$case" | cut -d: -f2)"
  rm -f artifacts/Rejected.dll
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/negative/$name.atto" artifacts/Rejected.dll \
    >/tmp/atto-negative.stdout 2>/tmp/atto-negative.stderr
  result=$?
  set -e
  test "$result" -eq 2 || { echo "NEGATIVE_STATUS_FAILED case=$name exit=$result"; exit 1; }
  grep -Fq "$diagnostic:" /tmp/atto-negative.stderr || {
    cat /tmp/atto-negative.stderr; echo "NEGATIVE_DIAGNOSTIC_FAILED case=$name"; exit 1;
  }
  test ! -e artifacts/Rejected.dll || { echo "NEGATIVE_ARTIFACT_LEAK case=$name"; exit 1; }
done
echo "ATTO_DIRECT_CIL_NEGATIVE_PASS checks=14"
# Exact canonical .atto conformance sources from atto-platform-conformance #102.
# This compiler cannot emit actor pipeline CIL: the only correct current
# response is an explicit unsupported-feature diagnostic, NO guest artifact.
for entry in \
  actor-forward:dd59abdb707d05dabac30139feb4bd03b60459d1 \
  actor-reverse:da3d759d56aac4cfd6919521df862edb594f426f \
  actor-missing-emits:6755a01ce43f7ac33f64d86d43dff7c7257185ca \
  actor-wrong-direction:12ee1a935c43477213604593d73b0cecf761fead \
  mixed-channel-stream-forward:6e2528f7c4b278c0fff9671c8c5985bfbd94adb1 \
  mixed-channel-stream-reverse:060b61b80cfb3946a9510edd1190be6256212424 \
  actor-duplicate-direction:d738937d72c791c41d55f088eaed65a5f9ed224f \
  actor-mixed-directions:1addd9cc44879d24f7e76b2d5eb69d6837a711ab; do
  case_name="${entry%%:*}"
  expected_blob="${entry#*:}"
  fixture="fixtures/pipe-rpipe-v1/${case_name}.atto"
  test "$(git hash-object "$fixture")" = "$expected_blob" || {
    echo "CANONICAL_FIXTURE_SHA_MISMATCH case=$case_name" >&2; exit 1;
  }
  rm -f artifacts/CanonicalPipeDenied.dll
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "$fixture" artifacts/CanonicalPipeDenied.dll \
    >/tmp/atto-canonical-pipe.stdout 2>/tmp/atto-canonical-pipe.stderr
  rc=$?
  set -e
  test "$rc" -eq 2 || {
    echo "CANONICAL_PIPE_STATUS_FAILED case=$case_name actual=$rc" >&2; exit 1;
  }
  grep -Fq "E_UNSUPPORTED_CONTINUOUS_PIPE:" /tmp/atto-canonical-pipe.stderr || {
    cat /tmp/atto-canonical-pipe.stderr >&2
    echo "CANONICAL_PIPE_DIAGNOSTIC_FAILED case=$case_name" >&2; exit 1;
  }
  test ! -e artifacts/CanonicalPipeDenied.dll || {
    echo "CANONICAL_PIPE_ARTIFACT_LEAK case=$case_name" >&2; exit 1;
  }
done
echo "ATTO_NET_CANONICAL_PIPE_FAILCLOSED_PASS checks=8"
# Compiler-owned graph planning checks; not an executable Atto pipeline.
dotnet run -c Release --project tests/Atto.Cil.Pipe.Plan/Atto.Cil.Pipe.Plan.csproj \
  > /tmp/atto-net-pipe-host.log
grep -Fx 'ATTO_NET_DIRECTED_PIPE_HOST_PASS checks=20' /tmp/atto-net-pipe-host.log
grep -Fx 'ATTO_NET_PIPE_GUEST_EXECUTION=NOT_EXECUTED' /tmp/atto-net-pipe-host.log
dotnet build "$host" -c Release
expect_42 CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-nativeaot
test -x /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host
expect_42 NATIVEAOT_GUEST /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host

# Additional genuine CIL guest executions; neither calculation is in C#.
for fixture in fixtures/cil/precedence.atto fixtures/cil/forward-call.atto; do
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  dotnet build "$host" -c Release
  expect_42 EXTRA_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
done
echo "ATTO_DIRECT_CIL_POSITIVE_PASS fixtures=3"
