#!/usr/bin/env bash
# Actual NativeAOT compiler + separately statically linked guest. No CoreCLR
# substitution. Source-local fixtures are generated deterministically.
set -Eeuo pipefail
command=compile
strict=0
if [[ "${1:-}" == "--strict" ]]; then command=compile-strict-i64; strict=1; shift; [[ "$#" -eq 1 ]]; fi
[[ "$#" -le 1 ]]
corpus=""
if [[ "$#" -eq 1 ]]; then corpus="$(realpath -- "$1")"; [[ -f "$corpus" ]]; fi
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
head="$(git rev-parse HEAD)"
tree="$(git rev-parse HEAD^{tree})"
[[ -z "$(git status --porcelain --untracked-files=all)" ]]
printf 'SOURCE_HEAD=%s SOURCE_TREE=%s SDK=%s\n' "$head" "$tree" "$(dotnet --version)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
rid=linux-x64
compiler_dir="$work/compiler"
compiler="$compiler_dir/oresnet"
host="$root/host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj"
dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r "$rid" -o "$compiler_dir" -warnaserror
bash verification/assert-linux-native-release.sh "$compiler"
pre='define dimension Time; define unit Second: Time; define unit Millis: Time = Second / 1000; type Positive = int where self > 0 and units in Second;'
cases=0
guest() {
  local name="$1" expr="$2" expected_status="$3" expected_text="$4" source="$work/$1.atto" generated="$work/$1/OresGenerated.dll" output="$work/$1/native"
  mkdir -p -- "$work/$name"
  if [[ -n "$corpus" ]]; then
    cp -- "$work/shared/$name.atto" "$source"
  else
  printf '%s\npub fnc main() -> int { return %s; }\n' "$pre" "$expr" >"$source"
  fi
  "$compiler" "$command" "$source" --out "$generated"
  [[ -s "$generated" ]]
  dotnet clean "$host" -c Release -r "$rid" "-p:GeneratedAssembly=$generated"
  dotnet publish "$host" -c Release -r "$rid" -o "$output" "-p:GeneratedAssembly=$generated" -warnaserror
  bash verification/assert-linux-native-release.sh "$output/oreslang-app"
  local status=0
  timeout --signal=TERM --kill-after=2s 10s "$output/oreslang-app" >"$work/stdout" 2>"$work/stderr" || status=$?
  [[ "$status" -eq "$expected_status" ]]
  if [[ "$expected_status" -eq 0 ]]; then
    cmp -s "$work/stdout" <(printf '%s\n' "$expected_text")
    [[ ! -s "$work/stderr" ]]
  else
    [[ ! -s "$work/stdout" ]]
    cmp -s "$work/stderr" <(printf '%s\n' "$expected_text")
  fi
  printf 'NATIVE_AOT_PASS %s exit=%s\n' "$name" "$status"
  sha256sum -- "$source" "$compiler" "$generated" "$output/oreslang-app"
  cases=$((cases+1))
}
reject() {
  local name="$1" expr="$2" diagnostic="$3" source="$work/$1.atto" status=0
  if [[ -n "$corpus" ]]; then
    cp -- "$work/shared/$name.atto" "$source"
  else
  printf '%s\npub fnc main() -> int { return %s; }\n' "$pre" "$expr" >"$source"
  fi
  mkdir -p -- "$work/$name"
  local rejected="$work/$name/OresGenerated.dll"
  "$compiler" "$command" "$source" --out "$rejected" >"$work/stdout" 2>"$work/stderr" || status=$?
  [[ "$status" -eq 1 && ! -e "$rejected" && ! -s "$work/stdout" ]]
  if [[ "$diagnostic" == "-" ]]; then [[ -s "$work/stderr" ]]; else grep -Fq "$diagnostic:" "$work/stderr"; fi
  printf 'NATIVE_AOT_REJECT %s %s\n' "$name" "$diagnostic"
  cases=$((cases+1))
}
if [[ -n "$corpus" ]]; then
  mkdir -p "$work/shared"
  if [[ "$strict" -eq 1 ]]; then
    node --input-type=module - "$corpus" "$work/shared" <<'STRICTJS'
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {join} from 'node:path';
const [corpus,out]=process.argv.slice(2);
const manifest=JSON.parse(readFileSync(corpus,'utf8'));
assert.equal(manifest.profile,'verified-core-strict-i64-v1');
const cases=manifest.cases.filter(c=>['arithmetic','boundaries'].includes(c.tier));
assert.equal(cases.length,21);
const seen=new Set(),lines=[];
for(const {id,source,expected:e} of cases){
 assert.match(id,/^[a-z0-9-]+$/);assert.ok(!seen.has(id));seen.add(id);
 writeFileSync(join(out,id+'.atto'),source,{flag:'wx'});
 if(e.kind==='value'){assert.match(e.decimal,/^-?[0-9]+$/);lines.push([id,'guest','0',e.decimal].join('\t'));}
 else{assert.equal(e.kind,'diagnostic');assert.match(e.category,/^E_[A-Z0-9_]+$/);lines.push([id,'reject','1',e.category].join('\t'));}
}
writeFileSync(join(out,'cases.tsv'),lines.join('\n')+'\n',{flag:'wx'});
STRICTJS
  else
  node --input-type=module - "$corpus" "$work/shared" <<'JS'
import assert from 'node:assert/strict';
import {writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {pathToFileURL} from 'node:url';
const [corpus,out]=process.argv.slice(2);
const {typedBindingCases:cases}=await import(pathToFileURL(corpus).href);
assert.equal(cases.length,24);
const errors={divisionByZero:"Oreslang division by zero",integerOverflow:"Oreslang arithmetic overflow",refinementViolation:"Oreslang refinement check failed"};
const lines=[];
for(const {source,text,expected:e} of cases){
 assert.match(source,/^generated-binding-[a-z-]+\.atto$/);
 const name=source.slice(0,-5);
 writeFileSync(join(out,source),text,{flag:"wx"});
 if(e.kind==="diagnostic"){
   assert.ok(e.category===null||/^E_[A-Z0-9_]+$/.test(e.category));
   lines.push([name,"reject","1",e.category??"-"].join("\t"));
 }else if(e.kind==="value"){
   assert.match(e.decimal,/^-?[0-9]+$/);
   lines.push([name,"guest","0",e.decimal].join("\t"));
 }else{
   assert.ok(Object.hasOwn(errors,e.category));
   lines.push([name,"guest","70",errors[e.category]].join("\t"));
 }
}
writeFileSync(join(out,"cases.tsv"),lines.join("\n")+"\n",{flag:"wx"});
JS
  fi
  while IFS=$'\t' read -r name kind status expected; do
    if [[ "$kind" == "reject" ]]; then reject "$name" "" "$expected"; else guest "$name" "" "$status" "$expected"; fi
  done <"$work/shared/cases.tsv"
  if [[ "$strict" -eq 1 ]]; then [[ "$cases" -eq 21 ]]; else [[ "$cases" -eq 24 ]]; fi
else
guest prefix '(<Positive>(42.<Second>)) / 1<Second>' 0 42
guest postfix '(42.<Second>::<Positive>) / 1<Second>' 0 42
guest keyword '(42.<Second> as Positive) / 1<Second>' 0 42
guest invalid-prefix '(<Positive>(0.<Second>)) / 1<Second>' 70 'Oreslang refinement check failed'
guest invalid-postfix '(0.<Second>::<Positive>) / 1<Second>' 70 'Oreslang refinement check failed'
guest invalid-keyword '(0.<Second> as Positive) / 1<Second>' 70 'Oreslang refinement check failed'
guest zero '1.<Second> / 0.<Second>' 70 'Oreslang division by zero'
reject erase '42.<Second>::<i64>' E_UNIT_ERASURE
reject retag '42.<Second>.<Second>' E_UNIT_ATTACHMENT
reject scale '(42<Millis> as Positive) / 1<Second>' E_UNIT_SCALE
[[ "$cases" -eq 10 ]]
fi
git diff --quiet -- . && git diff --cached --quiet -- .
printf 'NATIVE_AOT_VERIFIED %s cases\n' "$cases"
