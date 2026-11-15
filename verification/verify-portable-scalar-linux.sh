#!/usr/bin/env bash
# Full native .NET AOT guest gate for six canonical .ores scalar programs.
# This is deliberately not CIL-only, managed-JIT-only or a host arithmetic model.
set -euo pipefail
export LC_ALL=C
die() { printf 'NET_PORTABLE_NATIVE_NOT_ACCEPTED: %s\n' "$*" >&2; exit 1; }
cd "$(dirname "$0")/.."
[[ "${EXPECTED_SOURCE_SHA:-}" =~ ^[a-f0-9]{40}$ ]] || die "exact-head source SHA is required"
[[ "$(git rev-parse HEAD)" == "$EXPECTED_SOURCE_SHA" ]] || die "source head mismatch"
[[ -d tests/portable-scalar && ! -L tests/portable-scalar ]] || die "invalid source fixture directory"
[[ -z "$(git status --porcelain --untracked-files=normal -- tests/portable-scalar)" ]] || die "dirty scalar sources"
limit="$(command -v timeout || command -v gtimeout || true)"
[[ -n "$limit" ]] || die "bounded native verifier unavailable"
tmp="$(mktemp -d)" || die "cannot create verification workspace"
trap 'rm -rf "$tmp"' EXIT
chmod 700 "$tmp"
count=0
: > "$tmp/seen"
while IFS='|' read -r source oracle blob extra || [[ -n "$source" ]]; do
  [[ -z "$extra" && "$source" =~ ^tests/portable-scalar/(3[2-7])-[a-z0-9-]+\.ores$ ]] || die "unexpected portable scalar source"
  id="${BASH_REMATCH[1]}"
  [[ "$oracle" == 42 && "$blob" =~ ^[a-f0-9]{40}$ ]] || die "invalid source/oracle manifest entry"
  [[ -f "$source" && ! -L "$source" ]] || die "nonregular .ores fixture"
  git ls-files --error-unmatch -- "$source" >/dev/null || die "fixture not tracked"
  ! grep -Fqx "$source" "$tmp/seen" || die "duplicate fixture"
  printf '%s\n' "$source" >> "$tmp/seen"
  ((++count <= 6)) || die "more than six fixtures"
  [[ "$(git rev-parse "HEAD:$source")" == "$blob" ]] || die "canonical blob differs from Git"
  [[ "$(git hash-object -- "$source")" == "$blob" ]] || die "on-disk source differs from reviewed blob"
  # This builds a genuine NativeAOT oresnet compiler, emits CIL from the
  # unchanged .ores, links NativeAOT host, and verifies the final native app.
  "$limit" -k 15s 480s bash scripts/publish-linux.sh "$source" "artifacts/portable$id" > "$tmp/publish.stdout" 2>"$tmp/publish.stderr" || {
    cat "$tmp/publish.stderr" >&2
    die "native compiler / native host publish failed: $source"
  }
  native="artifacts/portable$id/linux-x64/oreslang-app"
  compiler="artifacts/compiler/linux-x64/oresnet"
  [[ -s "$native" && -x "$native" && ! -L "$native" ]] || die "no new native executable"
  bash verification/assert-linux-native-release.sh "$native" >/dev/null || die "managed host substituted for native"
  [[ -s "$compiler" && -x "$compiler" ]] || die "native Oreslang compiler missing"
  "$limit" -k 3s 20s "$native" > "$tmp/guest.stdout" 2> "$tmp/guest.stderr" || die "native guest nonzero exit: $source"
  printf '42\n' > "$tmp/expected"
  cmp -s "$tmp/expected" "$tmp/guest.stdout" || die "wrong native guest result: $source"
  [[ ! -s "$tmp/guest.stderr" ]] || die "unexpected native guest stderr"
  [[ "$(git hash-object -- "$source")" == "$blob" ]] || die "guest source mutated"
  printf 'NET_PORTABLE_NATIVE_CASE_PASS source=%s git_blob=%s compiler_sha256=%s artifact_sha256=%s\n' \
    "$source" "$blob" "$(sha256sum "$compiler" | cut -d ' ' -f 1)" "$(sha256sum "$native" | cut -d ' ' -f 1)"
done < tests/portable-scalar/oracles.tsv
[[ "$count" -eq 6 ]] || die "incomplete canonical native Oreslang scalar suite"
[[ "$(git ls-files 'tests/portable-scalar/*.ores' | wc -l | tr -d ' ')" == 6 ]] || die "unmanifested source"
[[ -z "$(git status --porcelain --untracked-files=normal -- tests/portable-scalar)" ]] || die "fixture source modified"
printf 'NET_PORTABLE_NATIVE_ACCEPTED source_sha=%s cases=6\n' "$EXPECTED_SOURCE_SHA"
