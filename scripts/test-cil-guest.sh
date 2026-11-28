#!/usr/bin/env bash
# Real Atto .atto -> direct ECMA-335 CIL -> managed CLR and NativeAOT.
set -euo pipefail
compiler=compiler/Atto.Cil.Compiler/Atto.Cil.Compiler.csproj
host=tests/Atto.Cil.Guest.Host/Atto.Cil.Guest.Host.csproj
guest=artifacts/AttoGuest.dll
mkdir -p artifacts
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
  bool-unsupported:E_RETURN_TYPE \
  no-main:E_ENTRY \
  checked-i64-division-zero:E_I64_DIVISION_BY_ZERO \
  checked-i64-division-overflow:E_I64_DIVISION_OVERFLOW \
  checked-i64-division-unproven:E_I64_DIVISION_UNVERIFIED \
  checked-i64-division-min-unproven:E_I64_DIVISION_UNVERIFIED \
  signed-min-underflow:E_LITERAL \
  float-result-in-int-return:E_RETURN_TYPE \
  float-result-as-int-arg:E_RETURN_TYPE \
  unsupported-float-addition:E_RETURN_TYPE; do
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
echo "ATTO_DIRECT_CIL_NEGATIVE_PASS checks=20"
dotnet build "$host" -c Release
expect_42 CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-nativeaot
test -x /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host
expect_42 NATIVEAOT_GUEST /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host

# Additional genuine CIL guest executions; neither calculation is in C#.
for fixture in fixtures/cil/precedence.atto fixtures/cil/forward-call.atto fixtures/cil/signed-min-boundary.atto fixtures/cil/checked-i64-division.atto; do
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  dotnet build "$host" -c Release
  expect_42 EXTRA_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
  case "$fixture" in
    fixtures/cil/signed-min-boundary.atto|fixtures/cil/checked-i64-division.atto)
      name="$(basename "$fixture" .atto)"
      artifact_dir="/tmp/atto-cil-$name-nativeaot"
      dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
        --self-contained true -o "$artifact_dir"
      test -x "$artifact_dir/Atto.Cil.Guest.Host"
      expect_42 "${name}_NATIVEAOT_GUEST" "$artifact_dir/Atto.Cil.Guest.Host"
      ;;
  esac
done
echo "ATTO_DIRECT_CIL_POSITIVE_PASS fixtures=5"

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



# Host words for synchronization, task scope, and generators cannot be
# impersonated by pure scalar CIL function, parameter or local identifiers.
# A rejected source may never publish a runnable guest DLL.
for name in sync task yield; do
  for site in function parameter local; do
    file="artifacts/negative-reserved-${name}-${site}.atto"
    rejected="artifacts/negative-reserved-${name}-${site}.dll"
    case "$site" in
      function)
        printf 'fnc %s(int x): int -> x; pub fnc main(): int -> 42 |> %s;\n' "$name" "$name" >"$file" ;;
      parameter)
        printf 'fnc f(int %s): int -> 42; pub fnc main(): int -> f(1);\n' "$name" >"$file" ;;
      local)
        printf 'pub fnc main(): int { val %s = 42; return %s; }\n' "$name" "$name" >"$file" ;;
    esac
    rm -f "$rejected"
    set +e
    dotnet run --no-build -c Release --project "$compiler" -- "$file" "$rejected" \
      > /tmp/atto-cil-reserved.stdout 2> /tmp/atto-cil-reserved.stderr
    status=$?
    set -e
    test "$status" -eq 2 && test ! -e "$rejected" \
      && test ! -s /tmp/atto-cil-reserved.stdout \
      && grep -Fq 'E_NAME:' /tmp/atto-cil-reserved.stderr || {
      echo "ATTO_CIL_RESERVED_WORD_ADMISSION_FAIL name=$name site=$site exit=$status" >&2
      exit 1
    }
    rm -f "$file"
  done
done
echo 'ATTO_CIL_AUTHORITY_WORD_MATRIX=9_NO_GUEST_OUTPUT'

# Exact comparator source, host grant is required, both execution modes.
test "$(git hash-object fixtures/cil/pipelines/comparator-function-pipeline.atto)" = a29743b5ca5c415fd5896186cdeb88e2eb406dcf
rm -f "$guest"
set +e
dotnet run --no-build -c Release --project "$compiler" -- fixtures/cil/pipelines/comparator-function-pipeline.atto "$guest" >/tmp/atto-comparator-denied.out 2>/tmp/atto-comparator-denied.err
rc=$?
set -e
test "$rc" -eq 2 && test ! -e "$guest" && grep -Fq E_PERMISSION: /tmp/atto-comparator-denied.err
dotnet run --no-build -c Release --project "$compiler" -- fixtures/cil/pipelines/comparator-function-pipeline.atto "$guest" --allow-stdio
printf '121\n' >/tmp/atto-comparator-expected.out
dotnet build "$host" -c Release -t:Rebuild
expect_pipeline_exit comparator-CORECLR 121 dotnet run --no-build -c Release --project "$host" >/tmp/atto-comparator-managed.out 2>/tmp/atto-comparator-managed.err
# Capture guest output independently of the shell helper's status message.
set +e
dotnet run --no-build -c Release --project "$host" >/tmp/atto-comparator-managed.out 2>/tmp/atto-comparator-managed.err
rc=$?
set -e
test "$rc" -eq 121 && cmp /tmp/atto-comparator-expected.out /tmp/atto-comparator-managed.out && test ! -s /tmp/atto-comparator-managed.err
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o /tmp/atto-comparator-aot
set +e
/tmp/atto-comparator-aot/Atto.Cil.Guest.Host >/tmp/atto-comparator-native.out 2>/tmp/atto-comparator-native.err
rc=$?
set -e
test "$rc" -eq 121 && cmp /tmp/atto-comparator-expected.out /tmp/atto-comparator-native.out && test ! -s /tmp/atto-comparator-native.err
echo ATTO_COMPARATOR_PIPELINE_CORECLR_NATIVEAOT_PASS


# Direct CIL named arguments: observe actual parameter slots and source effects.
named_work="$(mktemp -d)"
trap 'rm -rf "$named_work"' EXIT
for spec in 'f(b:2,a:4):42' 'f(b=2,a=4):42' 'f(b:2,a=4):42' 'f(b=2,a:4):42' 'f(4,b=2):42' 'f(b=4,a=2):24'; do
  expr="$(printf '%s' "$spec" | sed 's/:[^:]*$//')"
  expected="$(printf '%s' "$spec" | sed 's/.*://')"
  printf 'fnc f(int a,int b): int { return a*10+b; }\npub fnc main(): int { return %s; }\n' "$expr" > "$named_work/positive.atto"
  rm -f "$guest"
  dotnet run --no-build -c Release --project "$compiler" -- "$named_work/positive.atto" "$guest"
  test -s "$guest"
  dotnet build "$host" -c Release -t:Rebuild
  expect_pipeline_exit "named-$expr-CORECLR" "$expected" dotnet run --no-build -c Release --project "$host"
done

# Inner reordered call must not overwrite outer saved b; check both effects and result.
cat > "$named_work/nested.atto" <<'ATTO_NAMED'
fnc f(int a, int b): int { return a*10+b; }
fnc tick(int v): int { stdio.println(v); return v; }
pub fnc main(): int {
    return f(b=tick(2), a:f(b=tick(3), a=tick(1))-9);
}
ATTO_NAMED
printf '2\n3\n1\n' > "$named_work/expected"
rm -f "$guest"
dotnet run --no-build -c Release --project "$compiler" -- "$named_work/nested.atto" "$guest" --allow-stdio
dotnet build "$host" -c Release -t:Rebuild
check_named_effects() {
  local label="$1"; shift
  local status=0
  "$@" >"$named_work/stdout" 2>"$named_work/stderr" || status=$?
  test "$status" -eq 42
  cmp "$named_work/expected" "$named_work/stdout"
  test ! -s "$named_work/stderr"
  echo "ATTO_NAMED_NESTED_EFFECTS_EXECUTED $label"
}
check_named_effects CORECLR dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o "$named_work/aot"
check_named_effects NATIVEAOT "$named_work/aot/Atto.Cil.Guest.Host"

for spec in 'f(a=4,a:2):E_NAMED_DUP' 'f(4,a=2):E_NAMED_DUP' 'f(z=4,b:2):E_NAMED_UNKNOWN' 'f(a=4,2):E_NAMED_ORDER' 'f(a:=4,b=2):E_PAIR_UNSUPPORTED'; do
  expr="$(printf '%s' "$spec" | sed 's/:[^:]*$//')"
  code="$(printf '%s' "$spec" | sed 's/.*://')"
  printf 'fnc f(int a,int b): int { return a*10+b; }\npub fnc main(): int { return %s; }\n' "$expr" > "$named_work/negative.atto"
  rejected="$named_work/rejected.dll"
  rm -f "$rejected"
  status=0
  dotnet run --no-build -c Release --project "$compiler" -- "$named_work/negative.atto" "$rejected" >"$named_work/reject.out" 2>"$named_work/reject.err" || status=$?
  test "$status" -eq 2
  grep -Fq "$code:" "$named_work/reject.err"
  test ! -e "$rejected"
done
echo ATTO_DIRECT_CIL_NAMED_CALL_GUEST_PASS
