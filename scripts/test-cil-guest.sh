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
  bool-unsupported:E_ENTRY \
  bool-return-mismatch:E_TYPE \
  guard-not-bool:E_TYPE \
  mixed-equality:E_TYPE \
  unimplemented-host-io:E_SYMBOL \
  std-http-unsupported:E_LEX \
  bool-param-call-type:E_TYPE \
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
echo "ATTO_DIRECT_CIL_NEGATIVE_PASS checks=18"
dotnet build "$host" -c Release
expect_42 CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-nativeaot
test -x /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host
expect_42 NATIVEAOT_GUEST /tmp/atto-cil-nativeaot/Atto.Cil.Guest.Host

# Typed bool parameter ABI: compile a real Atto guest, then run the
# generated CIL under CoreCLR and NativeAOT. The host does not implement logic.
dotnet run --no-build -c Release --project "$compiler" -- \
  fixtures/cil/bool-parameter.atto "$guest"
test -s "$guest"
dotnet build "$host" -c Release
expect_42 BOOL_PARAMETER_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-bool-parameter-nativeaot
test -x /tmp/atto-cil-bool-parameter-nativeaot/Atto.Cil.Guest.Host
expect_42 BOOL_PARAMETER_NATIVEAOT_GUEST /tmp/atto-cil-bool-parameter-nativeaot/Atto.Cil.Guest.Host

# Additional genuine CIL guest executions; neither calculation is in C#.
for fixture in fixtures/cil/precedence.atto fixtures/cil/forward-call.atto; do
  dotnet run --no-build -c Release --project "$compiler" -- "$fixture" "$guest"
  dotnet build "$host" -c Release
  expect_42 EXTRA_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"
done
echo "ATTO_DIRECT_CIL_POSITIVE_PASS fixtures=3"

# Compile the byte-identical native Atto common-source policy definitions
# with the ACTUAL .NET CIL frontend. This validates compiled semantics but
# does NOT claim package-import linking or physical HTTP/filesystem access.
test "$(git hash-object stdlib/std/http_response_policy.atto)" = 22757dafe89bc9601d04c8726a09ddf676206c7e
test "$(git hash-object stdlib/std/fs_text_policy.atto)" = 0cd53216e91ef8de1a764ba5b80ce85c5796c1f2
cat stdlib/std/http_response_policy.atto \
    stdlib/std/fs_text_policy.atto \
    fixtures/cil/hosted-policy-main.atto > artifacts/hosted-policy-linked.atto
dotnet run --no-build -c Release --project "$compiler" -- \
    artifacts/hosted-policy-linked.atto "$guest"
test -s "$guest"
dotnet build "$host" -c Release
expect_42 HOSTED_POLICY_CORECLR_GUEST dotnet run --no-build -c Release --project "$host"

# Re-publish against the new generated Atto CIL, never reuse the earlier AOT
# artifact (which would still embed the old scalar fixture).
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-hosted-policy-nativeaot
test -x /tmp/atto-cil-hosted-policy-nativeaot/Atto.Cil.Guest.Host
expect_42 HOSTED_POLICY_NATIVEAOT_GUEST /tmp/atto-cil-hosted-policy-nativeaot/Atto.Cil.Guest.Host
echo "ATTO_HOSTED_POLICY_DIRECT_CIL_GUEST_PASS status=compiled-CIL-and-NativeAOT"

# Verify the full 56-vector reference registry as two compiled Atto guest
# functions with 28 guarded checks each (bounded, no interpreter).
test "$(git hash-object fixtures/cil/hosted-policy-vectors-main.atto)" = fab89196d07080d9ec2a9902220b82a5f13e7bff
cat stdlib/std/http_response_policy.atto \
    stdlib/std/fs_text_policy.atto \
    fixtures/cil/hosted-policy-vectors-main.atto > artifacts/hosted-policy-vectors-linked.atto
dotnet run --no-build -c Release --project "$compiler" -- \
    artifacts/hosted-policy-vectors-linked.atto "$guest"
test -s "$guest"
dotnet build "$host" -c Release
expect_42 HOSTED_POLICY_56_VECTORS_CORECLR dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-hosted-policy-vectors-nativeaot
test -x /tmp/atto-cil-hosted-policy-vectors-nativeaot/Atto.Cil.Guest.Host
expect_42 HOSTED_POLICY_56_VECTORS_NATIVEAOT /tmp/atto-cil-hosted-policy-vectors-nativeaot/Atto.Cil.Guest.Host
echo "ATTO_HOSTED_POLICY_VECTOR_PARITY_PASS signed_i64_cases=56"

# Compile the compiler-local .atto fs byte-I/O policy through the real
# Atto CIL frontend. This is NOT a filesystem syscall or std.fs import test.
cat stdlib/std/fs_io_policy.atto \
    fixtures/cil/fs-io-policy-main.atto > artifacts/fs-io-policy-linked.atto
dotnet run --no-build -c Release --project "$compiler" -- \
  artifacts/fs-io-policy-linked.atto "$guest"
test -s "$guest"
dotnet build "$host" -c Release
expect_42 FS_IO_POLICY_CORECLR dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-fs-io-policy-nativeaot
test -x /tmp/atto-cil-fs-io-policy-nativeaot/Atto.Cil.Guest.Host
expect_42 FS_IO_POLICY_NATIVEAOT /tmp/atto-cil-fs-io-policy-nativeaot/Atto.Cil.Guest.Host
echo "ATTO_FS_IO_POLICY_31_VECTORS_PASS scope=pure-atto-policy-only"

# Compile independently owned pure .atto fs API admission predicates.
# This is real generated CoreCLR/NativeAOT execution, NOT actual host file I/O.
cat stdlib/std/fs_api_policy.atto \
    fixtures/cil/fs-api-policy-main.atto > artifacts/fs-api-policy-linked.atto
dotnet run --no-build -c Release --project "$compiler" -- \
  artifacts/fs-api-policy-linked.atto "$guest"
test -s "$guest"
dotnet build "$host" -c Release
expect_42 FS_API_POLICY_CORECLR dotnet run --no-build -c Release --project "$host"
dotnet publish "$host" -c Release -r linux-x64 -p:PublishAot=true \
  --self-contained true -o /tmp/atto-cil-fs-api-policy-nativeaot
test -x /tmp/atto-cil-fs-api-policy-nativeaot/Atto.Cil.Guest.Host
expect_42 FS_API_POLICY_NATIVEAOT /tmp/atto-cil-fs-api-policy-nativeaot/Atto.Cil.Guest.Host
echo "ATTO_FS_API_POLICY_32_VECTORS_PASS scope=pure-atto-policy-only"
