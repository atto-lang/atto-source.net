#!/usr/bin/env bash
# Hermetic synthetic-Git regression tests for the source-local manifest verifier.
# Never copies or exports private Atto compiler/actor source or fixtures.
set -euo pipefail
source_root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
verifier="$source_root/scripts/verify-direct-cil-integration.sh"
test -f "$verifier" || { echo 'Missing source-local verifier' >&2; exit 1; }
work="$(mktemp -d)"
result="$work.result"
trap 'rm -rf "$work" "$result"' EXIT

git -C "$work" init -q
git -C "$work" config user.name 'Synthetic CI Test'
git -C "$work" config user.email 'synthetic@example.invalid'
mkdir -p "$work/fixtures/synthetic" "$work/verification" \
  "$work/scripts" "$work/compiler/Atto.Cil.Compiler"
cp "$verifier" "$work/scripts/verify-direct-cil-integration.sh"
for path in scripts/test-cil-guest.sh scripts/test-match-cil-guest.sh \
            scripts/test-stdio-cil-guest.sh compiler/Atto.Cil.Compiler/Program.cs; do
  printf 'synthetic non-Atto test placeholder %s\n' "$path" > "$work/$path"
done
printf '# Generated synthetic fixture manifest, no private source\n' \
  > "$work/verification/direct-cil-integration-blobs.tsv"
for n in $(seq 1 38); do
  printf -v path 'fixtures/synthetic/payload-%02d.atto' "$n"
  printf 'synthetic-fixture-%02d\n' "$n" > "$work/$path"
  sha="$(git -C "$work" hash-object --no-filters "$path")"
  printf '%s\t%s\n' "$sha" "$path" >> "$work/verification/direct-cil-integration-blobs.tsv"
done
git -C "$work" add -A
git -C "$work" commit -q -m 'synthetic no-source test fixture'

checks=0
assert_pass() {
  local label="$1"
  if ! (cd "$work" && bash "$verifier") > "$result" 2>&1; then
    echo "FAIL $label expected clean pass" >&2; cat "$result" >&2; exit 1
  fi
  if ! grep -Fq 'DIRECT_CIL_INTEGRATION_EXACT_BLOBS_PASS count=38' "$result"; then
    echo "FAIL $label missing source-local clean receipt" >&2
    cat "$result" >&2; exit 1
  fi
  checks=$((checks + 1))
  echo "PASS $label"
}
assert_reject() {
  local label="$1" diagnostic="$2"
  if (cd "$work" && bash "$verifier") > "$result" 2>&1; then
    echo "FAIL $label accepted deliberately corrupt synthetic data" >&2
    cat "$result" >&2; exit 1
  fi
  if ! grep -Fq "$diagnostic" "$result"; then
    echo "FAIL $label missing required diagnostic $diagnostic" >&2
    cat "$result" >&2; exit 1
  fi
  checks=$((checks + 1))
  echo "PASS $label $diagnostic"
  git -C "$work" reset -q --hard HEAD
  git -C "$work" clean -fdq
}
assert_pass 'clean-38-blobs'

printf 'tampered\n' >> "$work/fixtures/synthetic/payload-01.atto"
assert_reject 'payload-tamper' 'E_INTEGRATION_BLOB_MISMATCH'

tail -n 1 "$work/verification/direct-cil-integration-blobs.tsv" \
  >> "$work/verification/direct-cil-integration-blobs.tsv"
assert_reject 'duplicate-manifest-entry' 'E_INTEGRATION_MANIFEST_DUPLICATE'

sed -i '2d' "$work/verification/direct-cil-integration-blobs.tsv"
assert_reject 'truncated-manifest' 'E_INTEGRATION_MANIFEST_COUNT'

rm "$work/fixtures/synthetic/payload-01.atto"
ln -s /etc/hosts "$work/fixtures/synthetic/payload-01.atto"
assert_reject 'payload-symlink' 'E_INTEGRATION_BLOB_NOT_REGULAR'

chmod +x "$work/fixtures/synthetic/payload-01.atto"
git -C "$work" add fixtures/synthetic/payload-01.atto
assert_reject 'index-mode-tamper' 'E_INTEGRATION_BLOB_MISMATCH'

printf '\n// dirty C# compiler placeholder\n' >> "$work/compiler/Atto.Cil.Compiler/Program.cs"
assert_reject 'dirty-compiler-file' 'E_INTEGRATION_SOURCE_DIRTY'

printf '%040d\t../etc/passwd\n' 0 >> "$work/verification/direct-cil-integration-blobs.tsv"
assert_reject 'manifest-path-traversal' 'E_INTEGRATION_MANIFEST_INVALID'

rm "$work/fixtures/synthetic/payload-01.atto"
assert_reject 'missing-manifest-file-target' 'E_INTEGRATION_BLOB_NOT_REGULAR'

printf '%040d\truntime/not-allowed.atto\n' 0 >> "$work/verification/direct-cil-integration-blobs.tsv"
assert_reject 'manifest-out-of-scope-path' 'E_INTEGRATION_MANIFEST_SCOPE'

rm "$work/verification/direct-cil-integration-blobs.tsv"
assert_reject 'missing-manifest' 'E_INTEGRATION_MANIFEST_MISSING'

printf 'mutated test verifier\n' >> "$work/scripts/verify-direct-cil-integration.sh"
assert_reject 'dirty-verifier-file' 'E_INTEGRATION_SOURCE_DIRTY'

if [[ "$checks" != 12 ]]; then
  echo "FAILED: expected 12 synthetic Git cases, saw $checks" >&2; exit 1
fi
echo "ATTO_DIRECT_CIL_INTEGRITY_SYNTHETIC_PASS cases=$checks source_export=NONE"
