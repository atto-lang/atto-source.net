// Bootstrap-only source contract: never interpret as an executed Native AOT build.
import {test} from 'node:test';
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
