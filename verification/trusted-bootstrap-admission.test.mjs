// Bootstrap-only source contract: never interpret as an executed Native AOT build.
import {test} from 'node:test';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const repo='ores-truffle-oreslang/oreslang-source.net';
const cases=[
 ['trusted-source-local-linux-aot.yml','ORESLANG_NET_SELFHOSTED_LINUX','ORESLANG_NET_SELFHOSTED_LINUX_WORKFLOW_SHA','Linux'],
 ['trusted-source-local-windows-aot.yml','ORESLANG_NET_SELFHOSTED_WINDOWS','ORESLANG_NET_SELFHOSTED_WINDOWS_WORKFLOW_SHA','Windows'],
 ['trusted-source-local-fence-windows.yml','ORESLANG_NET_SELFHOSTED_WINDOWS','ORESLANG_NET_SELFHOSTED_WINDOWS_FENCE_WORKFLOW_SHA','Windows']
];
const v=k=>'$'+'{{ vars.'+k+' }}';
function verify(src,[file,enable,workflow,os]){
 assert.match(src,/^on:\r?\n  workflow_dispatch:\r?\n\r?\npermissions:/m);
 assert.equal((src.match(/^(?:on|["']on["']):/gm)||[]).length,1);
 assert.match(src,/^permissions:\r?\n  contents: read$/m);
 assert.ok(src.includes('cancel-in-progress: false'));
 assert.equal((src.match(/^\s+runs-on:$/gm)||[]).length,1);
 assert.match(src,/^      group: oreslang-net-aot-trusted$/m);
 assert.ok(src.includes('labels: [self-hosted, '+os+', X64, oreslang-net-aot]'));
 const pre=src.split(/^    runs-on:/m)[0];
 const path=repo+'/.github/workflows/'+file+'@refs/heads/main';
 for(const fragment of [
  "github.event_name == 'workflow_dispatch'",
  "github.ref == 'refs/heads/main'",
  'github.ref_protected == true',
  "github.repository == '"+repo+"'",
  "github.repository_id == '1419704375'",
  "github.workflow_ref == '"+path+"'",
  "vars."+enable+" == 'enabled'",
  "vars."+workflow+" != ''",
  'github.workflow_sha == vars.'+workflow,
  "vars."+enable+"_SHA != ''",
  "vars."+enable+"_TREE != ''"
 ])assert.ok(pre.includes(fragment),'missing admission: '+fragment);
 const preCheckout=src.split('uses: actions/checkout@')[0];
 for(const fragment of [
  'APPROVED_WORKFLOW_SHA: '+v(workflow),
  'APPROVED_HEAD: '+v(enable+'_SHA'),
  'APPROVED_TREE: '+v(enable+'_TREE'),
  'GITHUB_WORKFLOW_REF','GITHUB_WORKFLOW_SHA','GITHUB_REF_PROTECTED','APPROVED_WORKFLOW_SHA'
 ])assert.ok(preCheckout.includes(fragment),'missing precheckout: '+fragment);
 assert.ok(src.includes('ref: '+v(enable+'_SHA')));
 assert.ok(src.includes('persist-credentials: false'));
 assert.ok(src.includes('HEAD^{tree}'));
 assert.ok(src.includes('GITHUB_RUN_ATTEMPT'));
 for(const m of src.matchAll(/^\s*-\s+uses:\s+([^\s#]+)/gm))
  assert.match(m[1],/^[^@]+@[0-9a-f]{40}$/,'unpinned action');
}
for(const entry of cases){
 const yaml=readFileSync('.github/workflows/'+entry[0],'utf8');
 test(entry[0]+' admission',()=>verify(yaml,entry));
 test(entry[0]+' fail-closed mutations',()=>{
  const edits=[
   ['ref_protected == true','ref_protected == false'],
   ['github.workflow_sha == vars.','github.workflow_sha != vars.'],
   ['group: oreslang-net-aot-trusted','group: other'],
   ['persist-credentials: false','persist-credentials: true'],
   ['  workflow_dispatch:','  push:'],
   ['cancel-in-progress: false','cancel-in-progress: true'],
   ['APPROVED_WORKFLOW_SHA: '+v(entry[2]),'APPROVED_WORKFLOW_SHA: disabled'],
   ['ref: '+v(entry[1]+'_SHA'),'ref: main']
  ];
  for(const [oldText,newText] of edits){
   assert.ok(yaml.includes(oldText));
   assert.throws(()=>verify(yaml.replace(oldText,newText),entry),'accepted mutation: '+oldText);
  }
 });
}
test('evidence job watches and executes this bootstrap contract',()=>{
 const ci=readFileSync('.github/workflows/native-aot-evidence-contract.yml','utf8');
 for(const [file] of cases)assert.ok(ci.includes("      - '.github/workflows/"+file+"'"));
 assert.ok(ci.includes("      - 'verification/trusted-bootstrap-admission.test.mjs'"));
 assert.ok(ci.includes('run: node --test verification/trusted-bootstrap-admission.test.mjs'));
});

// Bootstrap workflow immutability: these three Git blob IDs are deliberately
// exact snapshots, not permissive YAML patterns. Any legitimate workflow edit
// requires a new independently reviewed snapshot AND an explicit pin update.
// Do not replace this with contains()/substring checks or a mutable branch ref.
const approvedBootstrapBlobs = new Map([
 ['trusted-source-local-linux-aot.yml', 'ba3d757bcadb60a4741a5d1aa7218c521b00bb29'],
 ['trusted-source-local-windows-aot.yml', '768955141b49d6c95e331195f08129cb2dd828a0'],
 ['trusted-source-local-fence-windows.yml', '277a4b92f563d0db50f51e6008592b23f838ac36']
]);
function gitBlobSha(source) {
 const bytes = Buffer.byteLength(source, 'utf8');
 return createHash('sha1').update('blob ' + bytes + '\0').update(source, 'utf8').digest('hex');
}
function verifyPinnedBootstrap(source, config) {
 verify(source, config);
 assert.equal(gitBlobSha(source), approvedBootstrapBlobs.get(config[0]),
  config[0] + ': trusted workflow bytes differ from pinned Git blob');
}
for(const entry of cases) {
 const original = readFileSync('.github/workflows/'+entry[0],'utf8');
 test(entry[0]+' pinned exact Git blob identity',()=>verifyPinnedBootstrap(original,entry));
 test(entry[0]+' rejects extra jobs, event keys, step injection, and guard weakening',()=>{
  const [file,enable,workflow] = entry;
  const mutations = [
   ['inline job', original + '\n  attacker: {runs-on: [self-hosted, Linux], steps: [{run: whoami}]}\n'],
   ['extra job', original + '\n  attacker:\n    runs-on: [self-hosted, Linux]\n    steps:\n      - run: whoami\n'],
   ['duplicate jobs mapping', original + '\njobs:\n  attacker: {runs-on: [self-hosted, Linux], steps: [{run: whoami}]}\n'],
   ['alternate event mapping', original + '\non :\n  pull_request_target:\n'],
   ['untrusted workflow bypass', original.replace(
    'github.workflow_sha == vars.'+workflow,
    "github.workflow_sha == vars."+workflow+" || github.actor == 'attacker'")],
   ['preflight extra executable step', original.replace(
    '      - name: ', '      - name: injected step\n        run: echo untrusted\n      - name: ')],
   ['untrusted event expression', original.replace(
    "github.event_name == 'workflow_dispatch'",
    "github.event_name == 'workflow_dispatch' || github.event_name == 'pull_request'")],
   ['source guard weakened', original.replace(
    "vars."+enable+"_SHA != ''", "vars."+enable+"_SHA != '' || github.actor != ''")],
   ['extra permission mapping', original + '\npermissions: write-all\n']
  ];
  for(const [label,mutated] of mutations) {
   assert.notEqual(mutated,original,'ineffective adversarial case: '+label);
   assert.throws(()=>verifyPinnedBootstrap(mutated,entry),
    file+': adversarial workflow mutation accepted: '+label);
  }
 });
}
