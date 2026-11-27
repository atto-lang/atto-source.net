#!/usr/bin/env bash
# Real Linux x64 NativeAOT scalar-copy ownership boundary; not general borrow parity.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]]
root="$(pwd -P)"
source=tests/fixtures/scalar-copy-nativeaot-v1
compiler=artifacts/compiler/linux-x64/oresnet
# Do not let an old artifact or a symlink supply a passing result.
[[ "$(realpath -m -- "$root/artifacts")" == "$root/artifacts" ]]
mkdir -p artifacts
[[ "$(realpath -e -- "$root/artifacts")" == "$root/artifacts" ]]
outroot="$(mktemp -d artifacts/scalar-copy-gate-XXXXXXXX)"
logs="$(mktemp -d)"
trap 'rm -rf -- "$logs" "$outroot"' EXIT
printf '42\n' >"$logs/expected.stdout"
failures=0
positive=0
negative=0

for fixture in shallow-i64 deep-i64-bool nested-call-copy; do
    dir="$logs/$fixture"
    mkdir "$dir"
    if ! bash scripts/publish-linux.sh "$source/$fixture.atto" "$outroot/$fixture" --structured; then
        printf 'NATIVE_COPY_BUILD_FAILURE %s\n' "$fixture" >&2
        failures=$((failures + 1))
        continue
    fi
    app="$outroot/$fixture/linux-x64/oreslang-app"
    if [[ ! -f "$app" || -L "$app" || ! -x "$app" || ! -s "$app" ]]; then
        printf 'NATIVE_COPY_ARTIFACT_FAILURE %s\n' "$fixture" >&2
        failures=$((failures + 1))
        continue
    fi
    status=0
    env -i PATH=/usr/bin:/bin "$app" >"$dir/stdout" 2>"$dir/stderr" || status=$?
    if [[ "$status" != 0 || -s "$dir/stderr" ]] || ! cmp -s "$logs/expected.stdout" "$dir/stdout"; then
        printf 'NATIVE_COPY_GUEST_FAILURE %s status=%s\n' "$fixture" "$status" >&2
        cat "$dir/stdout" "$dir/stderr" >&2
        failures=$((failures + 1))
        continue
    fi
    printf 'NATIVE_COPY_GUEST %s source_blob=%s\n' "$fixture" "$(git hash-object "$source/$fixture.atto")"
    sha256sum "$app" "$compiler"
    positive=$((positive + 1))
done

# A failed or missing final compiler cannot be replaced by a managed fallback.
[[ -f "$compiler" && ! -L "$compiler" && -x "$compiler" && -s "$compiler" ]]
bash verification/assert-linux-native-release.sh "$compiler"
for case in 'option-copy:E_COPY_UNSUPPORTED_TYPE' \
    'triple-bang:E_BANG_KEYWORD_WHITESPACE' \
    "unbound:Undefined local 'notDeclared'" \
    'borrow-through-copy:OWNERSHIP_BANG_BACKEND_UNSUPPORTED' \
    'move-through-copy:OWNERSHIP_BANG_BACKEND_UNSUPPORTED'; do
    fixture=${case%%:*}
    expected=${case#*:}
    dir="$logs/$fixture"
    mkdir "$dir"
    out="$dir/OresGenerated.dll"
    status=0
    "$compiler" compile-structured "$source/$fixture.atto" --out "$out" \
        >"$dir/stdout" 2>"$dir/stderr" || status=$?
    # Require a genuine typed CLI error, not a crash, successful compilation,
    # stale output, dangling symlink, or an error from a different fixture.
    if [[ "$status" != 1 || -e "$out" || -L "$out" || -s "$dir/stdout" || ! -s "$dir/stderr" ]] ||
        ! grep -Fq -- "$expected" "$dir/stderr"; then
        printf 'NATIVE_COPY_DENIAL_FAILURE %s status=%s expected=%s\n' "$fixture" "$status" "$expected" >&2
        cat "$dir/stdout" "$dir/stderr" >&2
        failures=$((failures + 1))
        continue
    fi
    printf 'NATIVE_COPY_NO_ARTIFACT %s source_blob=%s diagnostic=%s\n' \
        "$fixture" "$(git hash-object "$source/$fixture.atto")" "$expected"
    cat "$dir/stderr"
    negative=$((negative + 1))
done
printf 'NATIVE_COPY_SUMMARY positive=%s negative=%s failures=%s\n' "$positive" "$negative" "$failures"
[[ "$positive" == 3 && "$negative" == 5 && "$failures" == 0 ]]
