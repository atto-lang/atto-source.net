#!/usr/bin/env bash
# Actual compiler-emitted CIL, statically linked by NativeAOT.
set -euo pipefail
cd "$(dirname "$0")/../.."
test "$(uname -s)" = Linux
test "$(uname -m)" = x86_64
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
export ATTO_POLICY_WORK="$work"
node --input-type=module <<'JS'
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {join} from 'node:path';
import {arenaPolicyBatches} from './tests/arena-policy-differential-v1.mjs';
const pinned=(path,sha)=>{
 const b=readFileSync(path);
 assert.equal(createHash('sha1').update('blob '+b.length+'\0').update(b).digest('hex'),sha,path);
 return b.toString('utf8');
};
pinned('tests/arena-policy-differential-v1.mjs','f98055af7bb9b8952664946d2b2a1f3a735dc283');
const core=pinned('stdlib/policy/core/arena_limits.atto','5928e893a2ce1baadb0376a4ccb14f63249f77d4');
const life=pinned('stdlib/policy/alloc/arena_lifecycle.atto','e4795c6d4bd561be3d913cdc617d103d0e2828c8');
const fixed=pinned('tests/fixtures/arena-quota-lifecycle-guest.atto','2b1eb28f07b792f2727abe9a5691849f024e304a');
const prefix='pub fnc main()';
const rename=(s,n)=>{
 assert.equal(s.split(prefix).length,2);
 return s.replace(prefix,'pub fnc '+n+'()');
};
const batches=arenaPolicyBatches();
assert.equal(batches.length,22);
assert.equal(batches.reduce((n,b)=>n+b.count,0),352);
const helpers=[rename(fixed,'policy_fixed'),...batches.map((b,i)=>rename(b.source,'policy_batch_'+i))];
const main='pub fnc main(): int {\n if policy_fixed() != 100 then return 1; fi\n'+
 batches.map((b,i)=>' if policy_batch_'+i+'() != 100 then return '+(i+2)+'; fi').join('\n')+'\n return 100;\n}\n';
const good=[core,life,...helpers,main].join('\n');
const marker='arena_end_offset(0, 0, 1, 0) != 0';
assert.equal(good.split(marker).length,2);
writeFileSync(join(process.env.ATTO_POLICY_WORK,'good.atto'),good);
writeFileSync(join(process.env.ATTO_POLICY_WORK,'wrong.atto'),good.replace(marker,'arena_end_offset(0, 0, 1, 0) != 1'));
JS
compiler=compiler/Ores.Net.Compiler
host=host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj
dotnet build "$compiler" -c Release
for case in good wrong; do
  out="$work/$case"
  mkdir "$out"
  assembly="$out/OresGenerated.dll"
  dotnet run --project "$compiler" -c Release --no-build -- compile-structured "$work/$case.atto" --out "$assembly"
  test -s "$assembly"
  sha256sum "$work/$case.atto" "$assembly"
  dotnet clean "$host" -c Release -r linux-x64 "-p:GeneratedAssembly=$assembly"
  dotnet publish "$host" -c Release -r linux-x64 -o "$out/native" "-p:GeneratedAssembly=$assembly"
  app="$out/native/oreslang-app"
  file "$app" | grep -E 'ELF 64-bit.*x86-64'
  test -z "$(find "$out/native" -name '*.dll' -print -quit)"
  sha256sum "$app"
  env -i PATH=/usr/bin:/bin "$app" > "$out/stdout" 2> "$out/stderr"
  test ! -s "$out/stderr"
  if [[ "$case" == good ]]; then expected=100; else expected=1; fi
  printf '%s\n' "$expected" > "$out/expected"
  cmp "$out/expected" "$out/stdout"
done
echo 'NET_ARENA_POLICY_NATIVEAOT_PASS assertions=408 wrong_oracle=1'
