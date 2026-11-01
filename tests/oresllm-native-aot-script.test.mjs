// HOST-ONLY guard on the authored Linux NativeAOT guest script, NOT execution.
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
const path='verification/oresllm-eight-scalar-native-aot.sh';
const script=readFileSync(path,'utf8');
const workflow=readFileSync('.github/workflows/oresllm-eight-scalar-native-aot.yml','utf8');
const existingWorkflow=readFileSync('.github/workflows/strict-linux-native-aot.yml','utf8');
const expectations=new Map([
 ['literal-42',['accept','42']],
 ['precedence',['accept','28']],
 ['unary',['accept','-21']],
 ['i64-min',['accept','-9223372036854775808']],
 ['i64-max',['accept','9223372036854775807']],
 ['max-minus-one',['accept','9223372036854775806']],
 ['overflow-add',['overflow','']],
 ['overflow-multiply',['overflow','']]
]);
const rows=[...script.matchAll(/^verify_case "([^"]+)" "(pub fnc main\(\): int \{ return [^"]+ \})" "([0-9a-f]{64})" "(accept|overflow)" "([^"]*)"$/gm)];
test('eight native-AOT fixtures remain distinct, complete, content-pinned',()=>{
 assert.equal(rows.length,8);
 const seen=new Set();
 for(const m of rows){
  const [,id,source,digest,kind,expected]=m;
  assert.equal(seen.has(id),false,id);seen.add(id);
  assert.deepEqual([kind,expected],expectations.get(id));
  assert.equal(createHash('sha256').update(source+'\n').digest('hex'),digest);
 }
 assert.deepEqual([...seen].sort(),[...expectations.keys()].sort());
});
test('publisher compiles actual production source and checks native ELF',()=>{
 assert.match(script,/bash scripts\/publish-linux\.sh "\$source" artifacts\/oresllm_native_scalar/);
 assert.match(script,/bash verification\/assert-linux-native-release\.sh "\$app"/);
 assert.match(script,/sha256sum --status -c -/);
 assert.match(script,/cmp --silent "\$work\/stdout" "\$work\/expected"/);
 assert.match(script,/cmp --silent "\$work\/stderr" "\$work\/expected"/);
 assert.match(script,/\[\[ "\$kind" == overflow && "\$status" -eq 70 && ! -s "\$work\/stdout" \]\]/);
 assert.match(script,/NATIVE_AOT_EIGHT_SCALAR_CASES_OK\|cases=8/);
});
test('NativeAOT CI runs only by explicit dispatch on same private source repository',()=>{
 assert.match(workflow,/^\s*workflow_dispatch:/m);
 assert.doesNotMatch(workflow,/\bpull_request_target:|\bworkflow_run:/);
 assert.doesNotMatch(workflow,/^\s*push:|^\s*pull_request:/m);
 assert.match(workflow,/permissions:\s*\n\s*contents: read/);
 assert.match(workflow,/persist-credentials: false/);
 assert.match(workflow,/git rev-parse HEAD/);
 assert.match(workflow,/setup-dotnet@[0-9a-f]{40}/);
 assert.match(workflow,/bash verification\/oresllm-eight-scalar-native-aot\.sh/);
 assert.match(existingWorkflow,/if: github\.event_name == 'workflow_dispatch'/);
 assert.match(existingWorkflow,/bash verification\/oresllm-eight-scalar-native-aot\.sh/);
});
test('host guard does not claim NativeAOT execution',()=>{
 assert.match(script,/NOT a CIL reference oracle/);
 assert.match(workflow,/Never source-only certification/);
 assert.doesNotMatch(workflow,/source.*mirror|cross.org.*source.*copy/i);
});
