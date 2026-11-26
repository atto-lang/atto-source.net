#!/usr/bin/env bash
# Source-local exact-Git-blob gate; never authorizes cross-org source export.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
manifest=verification/direct-cil-integration-blobs.tsv
test -f "$manifest" || { echo "E_INTEGRATION_MANIFEST_MISSING" >&2; exit 1; }
declare -A seen=()
count=0
while IFS=$'\t' read -r expected path || [[ -n "$expected$path" ]]; do
  if [[ -z "$expected" || "$expected" == \#* ]]; then continue; fi
  if [[ ! "$expected" =~ ^[0-9a-f]{40}$ ]] ||
     [[ ! "$path" =~ ^[a-zA-Z0-9._/-]+$ ]] ||
     [[ "$path" == /* || "$path" == *..* ]]; then
    echo "E_INTEGRATION_MANIFEST_INVALID" >&2; exit 1
  fi
  case "$path" in
    .github/workflows/*|docs/*|fixtures/*|tests/Atto.Cil.Float.Guest.Host/*) ;;
    *) echo "E_INTEGRATION_MANIFEST_SCOPE=$path" >&2; exit 1 ;;
  esac
  if [[ -n "${seen[$path]+present}" ]]; then
    echo "E_INTEGRATION_MANIFEST_DUPLICATE=$path" >&2; exit 1
  fi
  seen[$path]=1
  if [[ ! -f "$path" || -L "$path" ]]; then
    echo "E_INTEGRATION_BLOB_NOT_REGULAR=$path" >&2; exit 1
  fi
  blob="$(git hash-object --no-filters "$path")"
  committed="$(git rev-parse "HEAD:$path")"
  tracked="$(git ls-files --stage -- "$path" | cut -f1)"
  if [[ "$blob" != "$expected" || "$committed" != "$expected" ||
        "$tracked" != "100644 $expected 0" ]]; then
    echo "E_INTEGRATION_BLOB_MISMATCH=$path expected=$expected actual=$blob" >&2
    exit 1
  fi
  count=$((count + 1))
done < "$manifest"
if [[ "$count" -ne 38 ]]; then
  echo "E_INTEGRATION_MANIFEST_COUNT=$count expected=38" >&2
  exit 1
fi
for path in \
    compiler/Atto.Cil.Compiler/Program.cs \
    scripts/test-cil-guest.sh \
    scripts/test-match-cil-guest.sh \
    scripts/test-stdio-cil-guest.sh \
    scripts/verify-direct-cil-integration.sh \
    verification/direct-cil-integration-blobs.tsv; do
  test -f "$path" && test ! -L "$path"
  test "$(git hash-object --no-filters "$path")" = "$(git rev-parse "HEAD:$path")" || {
    echo "E_INTEGRATION_SOURCE_DIRTY=$path" >&2; exit 1;
  }
done
echo "DIRECT_CIL_INTEGRATION_EXACT_BLOBS_PASS count=$count head=$(git rev-parse HEAD) tree=$(git rev-parse 'HEAD^{tree}')"
