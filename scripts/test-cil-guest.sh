#!/usr/bin/env bash
# Real Atto .atto -> direct ECMA-335 CIL -> managed CLR and NativeAOT.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts
# The compiler now intentionally refuses all DLL overwrites; test harness
# removes only its own known output before deliberate new compilations.
rm -f "$guest"

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
echo "ATTO_DIRECT_CIL_NEGATIVE_PASS checks=12"
dotnet build "$host" -c Release
expect_42 CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-nativeaot
test -x /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host
expect_42 NATIVEAOT_GUEST /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host

# Additional genuine CIL guest executions; neither calculation is in C#.
for fixture in fixtures/cil/precedence.atto fixtures/cil/forward-call.atto; do
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  dotnet build "$host" -c Release
  expect_42 EXTRA_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
done
echo "ATTO_DIRECT_CIL_POSITIVE_PASS fixtures=3"

# Functional pipeline conformance is compiled from .atto source, not evaluated
# in shell/C#/Python. The same produced CIL is executed under CoreCLR and,
# for both canonical 83-result cases, a freshly published NativeAOT host.
expect_pipeline_exit() {
  local label="$1" expected="$2"; shift 2
  local actual
  set +e
  "$@"
  actual=$?
  set -e
  test "$actual" -eq "$expected" || {
    echo "ATTO_PIPELINE_EXECUTION_FAILED label=$label expected=$expected actual=$actual" >&2
    exit 1
  }
  echo "ATTO_PIPELINE_EXECUTED label=$label guest_exit=$actual"
}
for spec in \
  block-body-83:83 \
  inline-arrow-83:83 \
  reverse-42:42 \
  grouped-42:42; do
  name="$(printf '%s' "$spec" | cut -d: -f1)"
  expected="$(printf '%s' "$spec" | cut -d: -f2)"
  fixture="fixtures/cil/pipelines/$name.atto"
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  test -s "$guest"
  before="$(sha256sum "$guest" | cut -d' ' -f1)"
  # An existing DLL must not be replaced, even by a *valid* second compile.
  # A competing publisher never gets an implicit overwrite capability.
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest" \
    > /tmp/atto-pipeline-nooverwrite.stdout 2>/tmp/atto-pipeline-nooverwrite.stderr
  nooverwrite_status=$?
  set -e
  test "$nooverwrite_status" -eq 2 || {
    echo "ATTO_PIPELINE_EXISTING_DLL_NOT_REJECTED fixture=$name exit=$nooverwrite_status" >&2
    exit 1
  }
  grep -Fq "E_OUTPUT:" /tmp/atto-pipeline-nooverwrite.stderr
  after="$(sha256sum "$guest" | cut -d' ' -f1)"
  test "$before" = "$after" || {
    echo "ATTO_PIPELINE_EXISTING_DLL_CLOBBERED fixture=$name" >&2
    exit 1
  }
  # Exact same source recompiled to a fresh destination must remain
  # deterministic; the production compiler itself must not overwrite.
  replay="artifacts/ReplayPipeline.dll"
  rm -f "$replay"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$replay"
  test "$before" = "$(sha256sum "$replay" | cut -d' ' -f1)" || {
    echo "ATTO_PIPELINE_NONDETERMINISTIC_FRESH_DLL fixture=$name" >&2
    exit 1
  }
  test "$before" = "$after" || {
    echo "ATTO_PIPELINE_NONDETERMINISTIC_CIL fixture=$name" >&2
    exit 1
  }
  dotnet build "$host" -c Release -t:Rebuild
  expect_pipeline_exit "$name-CORECLR" "$expected" \
    dotnet run --no-build -c Release --project "$host"
  if [[ "$expected" -eq 83 ]]; then
    dotnet publish "$host" -c Release -r linux-x64 \
      -p:PublishAot=true --self-contained true \
      -o "/tmp/atto-cil-pipeline-$name"
    expect_pipeline_exit "$name-NATIVEAOT" "$expected" \
      "/tmp/atto-cil-pipeline-$name/Atto.Cil.Guest.Host"
  fi
done
echo "ATTO_PIPELINE_POSITIVE_GUEST_PASS cases=4 nativeaot_cases=2"

# Seven source byte-pinned supplemental spec edge cases. These are compiler
# and real guest tests, never a host-calculated reference model.
while IFS=: read -r name sha; do
  actual="$(git hash-object "fixtures/cil/pipelines/edge/$name.atto")"
  test "$actual" = "$sha" || {
    echo "ATTO_CIL_EDGE_FIXTURE_DRIFT name=$name actual=$actual" >&2
    exit 2
  }
done <<'EDGE_BLOBS'
two-argument-61:4089c524db1d076593994ad65acb5ca7f90eea32
preapplied-grouped-42:9a89ada18865fead0d92a7de75415957fbb5f67f
signed-i64-min:bbc22347d7ccb0a5be1a7c2193f11c5f2f4805cc
checked-overflow:59bb890b54addc1659065ab55cd565771523bfce
reject-i64-positive-overflow:2e584ee01ee74a3a31e282cf0116957f9ff95bd1
reject-i64-negative-underflow:8b63b45e406db3e19aa793e0f7a627914623b9e0
reject-reserved-unsafe:b159def3fa26ca6023395e1c546b95c1b929fd14
EDGE_BLOBS
for case in two-argument-61:61 preapplied-grouped-42:42; do
  IFS=: read -r name expected <<< "$case"
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/pipelines/edge/$name.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  expect_pipeline_exit "$name-EDGE-CORECLR" "$expected" \
    dotnet run --no-build -c Release --project "$host"
  dotnet publish "$host" -c Release -r linux-x64 \
    -p:PublishAot=true --self-contained true \
    -o "/tmp/atto-cil-edge-$name"
  expect_pipeline_exit "$name-EDGE-NATIVEAOT" "$expected" \
    "/tmp/atto-cil-edge-$name/Atto.Cil.Guest.Host"
done
echo 'ATTO_CIL_EDGE_MULTI_ARGUMENT_NATIVE=PASS expected=61,42'


# The entire signed-i64 value cannot be transported through an OS process
# exit code. Observe the *compiled CIL method* via a fixed .NET bridge
# that formats the returned long without calculating the guest expression.
value_host=tests/Atto.Cil.Value.Host/Atto.Cil.Value.Host.csproj
rm -f "$guest"
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/pipelines/edge/signed-i64-min.atto "$guest"
dotnet build "$value_host" -c Release -t:Rebuild
actual_min="$(dotnet run --no-build -c Release --project "$value_host" \
  2>/tmp/atto-cil-min.stderr)"
test "$actual_min" = "-9223372036854775808" || {
  echo "ATTO_CIL_I64_MIN_WRONG_VALUE observed=$actual_min" >&2; exit 1;
}
test ! -s /tmp/atto-cil-min.stderr
echo "ATTO_CIL_I64_MIN_CORECLR=EXECUTED"
dotnet publish "$value_host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-i64-min-aot
aot_min="$(/tmp/atto-cil-i64-min-aot/Atto.Cil.Value.Host)"
test "$aot_min" = "-9223372036854775808" || {
  echo "ATTO_CIL_I64_MIN_NATIVEAOT_WRONG_VALUE observed=$aot_min" >&2; exit 1;
}
echo "ATTO_CIL_I64_MIN_NATIVEAOT=EXECUTED"

# Checked overflow must be rejected by the emitted CLR arithmetic itself.
rm -f "$guest"
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/pipelines/edge/checked-overflow.atto "$guest"
dotnet build "$value_host" -c Release -t:Rebuild
set +e
dotnet run --no-build -c Release --project "$value_host" \
  > /tmp/atto-cil-overflow.stdout 2>/tmp/atto-cil-overflow.stderr
overflow_status=$?
set -e
test "$overflow_status" -ne 0 || {
  echo "ATTO_CIL_CHECKED_OVERFLOW_WRAPPED" >&2; exit 1;
}
test ! -s /tmp/atto-cil-overflow.stdout
grep -Fq 'OverflowException' /tmp/atto-cil-overflow.stderr || {
  echo "ATTO_CIL_CHECKED_OVERFLOW_WRONG_DIAGNOSTIC" >&2; exit 1;
}
echo "ATTO_CIL_CHECKED_OVERFLOW_TRAPPED=EXECUTED"

# A rejected source must not delete or rewrite an unrelated preexisting DLL.
sentinel=artifacts/AttoExistingSentinel.dll
printf 'do-not-replace-atto-binary\n' > "$sentinel"
sentinel_before="$(sha256sum "$sentinel" | cut -d' ' -f1)"
set +e
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/pipelines/negative/wrong-type.atto "$sentinel" \
  >/tmp/atto-cil-preserve.stdout 2>/tmp/atto-cil-preserve.stderr
preserve_rc=$?
set -e
test "$preserve_rc" -eq 2
grep -Fq 'E_TYPE:' /tmp/atto-cil-preserve.stderr
test "$sentinel_before" = "$(sha256sum "$sentinel" | cut -d' ' -f1)" || {
  echo "ATTO_CIL_NEGATIVE_DELETED_PREEXISTING_DLL" >&2; exit 1;
}
echo "ATTO_CIL_NEGATIVE_PRESERVES_PREEXISTING_OUTPUT=PASS"

# Exclusive temporary creation: an attacker-planted .tmp may not be truncated.
temp=artifacts/AttoBlocked.dll.tmp
blocked=artifacts/AttoBlocked.dll
rm -f "$temp" "$blocked"
printf 'do-not-truncate\n' > "$temp"
set +e
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/pipelines/block-body-83.atto "$blocked" \
  >/tmp/atto-cil-temp.stdout 2>/tmp/atto-cil-temp.stderr
blocked_rc=$?
set -e
test "$blocked_rc" -ne 0 && test ! -e "$blocked"
test "$(cat "$temp")" = "do-not-truncate"
rm -f "$temp"
echo "ATTO_CIL_EXCLUSIVE_TEMP_CREATE=PASS"


for spec in \
  mixed-forward-reverse:E_PARSE \
  mixed-reverse-forward:E_PARSE \
  missing-semicolon:E_PARSE \
  wrong-type:E_TYPE \
  unknown-call:E_SYMBOL \
  arity:E_ARITY \
  bad-callee:E_PARSE \
  forward-local:E_SYMBOL \
  duplicate-local:E_DUP_LOCAL \
  arrow-brace-no-return:E_PARSE \
  signed-int64-underflow:E_LITERAL \
  reserved-authority:E_NAME; do
  name="$(printf '%s' "$spec" | cut -d: -f1)"
  diagnostic="$(printf '%s' "$spec" | cut -d: -f2)"
  reject="artifacts/RejectedPipeline.dll"
  rm -f "$reject"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/pipelines/negative/$name.atto" "$reject" \
    > /tmp/atto-pipeline-negative.stdout \
    2> /tmp/atto-pipeline-negative.stderr
  actual=$?
  set -e
  test "$actual" -eq 2 || {
    echo "PIPELINE_NEGATIVE_WRONG_STATUS case=$name actual=$actual" >&2
    exit 1
  }
  grep -Fq "$diagnostic:" /tmp/atto-pipeline-negative.stderr || {
    cat /tmp/atto-pipeline-negative.stderr
    echo "PIPELINE_NEGATIVE_WRONG_DIAGNOSTIC case=$name" >&2
    exit 1
  }
  test ! -e "$reject" || {
    echo "PIPELINE_NEGATIVE_ARTIFACT_LEAK case=$name" >&2
    exit 1
  }
done
echo "ATTO_PIPELINE_NEGATIVE_PASS cases=12"

for case in \
  reject-i64-positive-overflow:E_LITERAL \
  reject-i64-negative-underflow:E_LITERAL \
  reject-reserved-unsafe:E_NAME; do
  IFS=: read -r name code <<< "$case"
  rejected="artifacts/RejectedEdge.dll"
  rm -f "$rejected"
  set +e
  dotnet run --no-build -c Release --project "$compiler" -- \
    "fixtures/cil/pipelines/edge/$name.atto" "$rejected" \
    > /tmp/atto-edge-rejected.stdout 2> /tmp/atto-edge-rejected.stderr
  status=$?
  set -e
  test "$status" -eq 2 && test ! -e "$rejected" \
    && test ! -s /tmp/atto-edge-rejected.stdout \
    && grep -Fq "$code:" /tmp/atto-edge-rejected.stderr || {
    echo "ATTO_CIL_EDGE_NEGATIVE_LEAK name=$name status=$status" >&2
    exit 1
  }
done
echo 'ATTO_CIL_EDGE_NEGATIVE=PASS cases=3'

