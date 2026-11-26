#!/usr/bin/env bash
set -euo pipefail
# Actual generated CIL is linked into a fresh NativeAOT host per fixture.
# No reference evaluator, dynamic loading, or managed execution fallback.
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
# Explicit RID and isolated output keep the executed compiler unambiguous.
dotnet build compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r linux-x64 -o "$tmp/compiler"
compiler="$tmp/compiler/oresnet.dll"
test -f "$compiler"
test -f "$tmp/compiler/oresnet.runtimeconfig.json"
printf 'TUPLE_COMPILER=%s\n' "$compiler"
dotnet restore host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj -r linux-x64
python3 - "$tmp" <<'PY'
import json,pathlib,sys
root=pathlib.Path(sys.argv[1])
c=json.load(open("verification/tuple-pattern-native-corpus.json"))
for kind in ("positive","negative","overflow"):
    for f in c[kind]:
        d=root/kind/f["id"];d.mkdir(parents=True)
        (d/"main.atto").write_bytes(f["source"].encode())
        if kind=="positive":(d/"expected").write_text(str(f["expected"])+"\n")
PY
for d in "$tmp"/negative/*; do
  if dotnet "$compiler" compile "$d/main.atto" --out "$d/OresGenerated.dll" >"$d/out" 2>"$d/err"; then
    echo "unexpected acceptance $d" >&2; exit 1
  fi
  test ! -e "$d/OresGenerated.dll"
  grep -Eq 'E_TUPLE_PATTERN|Expected.*\)|Expected integer' "$d/err"
done
for kind in positive overflow; do
  for d in "$tmp"/"$kind"/*; do
    dotnet "$compiler" compile "$d/main.atto" --out "$d/OresGenerated.dll"
    dotnet clean host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj -c Release -r linux-x64 "-p:GeneratedAssembly=$d/OresGenerated.dll"
    dotnet publish host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj -c Release -r linux-x64 -o "$d/native" "-p:GeneratedAssembly=$d/OresGenerated.dll" -warnaserror
    test -x "$d/native/oreslang-app"
    test "$(head -c4 "$d/native/oreslang-app" | od -An -tx1 | tr -d ' \n')" = 7f454c46
    test -z "$(find "$d/native" -name '*.dll' -o -name '*.runtimeconfig.json')"
    if [[ "$kind" == positive ]]; then
      "$d/native/oreslang-app" >"$d/actual" 2>"$d/err"
      cmp "$d/expected" "$d/actual"
      test ! -s "$d/err"
    else
      if "$d/native/oreslang-app" >"$d/actual" 2>"$d/err"; then echo "overflow returned normally" >&2;exit 1;fi
      test ! -s "$d/actual"
      grep -Ei 'overflow|arithmetic' "$d/err"
    fi
    echo "NATIVE_TUPLE_PASS $kind $(basename "$d")"
  done
done
echo "NET_TUPLE_NATIVE_PASS 13 shared + 8 rejection + 2 overflow"
