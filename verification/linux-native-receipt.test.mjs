// Synthetic evidence-contract tests only; /bin/true is not an Oreslang AOT build.
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync, mkdirSync, writeFileSync, copyFileSync, rmSync, symlinkSync, readFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join, dirname} from 'node:path';
import {execFileSync} from 'node:child_process';
import {createReceipt, binaries, sourceRepository} from './write-linux-native-receipt.mjs';

function fixture(action) {
  const root=mkdtempSync(join(tmpdir(),'ores-receipt-contract-'));
  const put=(p,s)=>{mkdirSync(dirname(join(root,p)),{recursive:true});writeFileSync(join(root,p),s);};
  const git=(...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8'}).trim();
  try {
    put('.gitignore','artifacts/\n');put('source.ores','synthetic test only\n');
    put('verification/assert-linux-native-release.sh',readFileSync(new URL('./assert-linux-native-release.sh',import.meta.url)));
    git('init','-q');git('add','.');git('-c','user.name=Test','-c','user.email=test@local','commit','-qm','Synthetic receipt fixture');
    for(const p of binaries){mkdirSync(dirname(join(root,p)),{recursive:true});copyFileSync('/bin/true',join(root,p));}
    const arithmetic={schema:'oreslang.net.linux_native_arithmetic_v1',division_rejection_exit:1,
      division_emitted_cil:false,overflow_exit:70,outputs:[14,42,40,45],native_application_count:5};
    put('artifacts/evidence/linux-native-arithmetic.json',JSON.stringify(arithmetic));
    const env={EXPECTED_HEAD:git('rev-parse','HEAD'),GITHUB_REPOSITORY:sourceRepository,
      GITHUB_RUN_ID:'123',GITHUB_RUN_ATTEMPT:'1',GITHUB_WORKFLOW_SHA:'a'.repeat(40),GITHUB_WORKFLOW_REF:'synthetic'};
    action({root,env,arithmetic,put});
  } finally {rmSync(root,{recursive:true,force:true});}
}
test('receipt binds source identity, arithmetic and all six binary hashes',()=>fixture(({root,env})=>{
  const r=createReceipt(root,env);assert.equal(r.source_head_sha,env.EXPECTED_HEAD);
  assert.equal(Object.keys(r.binary_sha256).length,6);assert.equal(r.arithmetic.overflow_exit,70);
  assert.equal(r.independently_verified,false);
}));
test('missing fifth application cannot produce receipt',()=>fixture(({root,env})=>{
  rmSync(join(root,binaries[5]));assert.throws(()=>createReceipt(root,env));
}));
test('symlinked binary cannot produce receipt',()=>fixture(({root,env})=>{
  rmSync(join(root,binaries[5]));symlinkSync('/bin/true',join(root,binaries[5]));assert.throws(()=>createReceipt(root,env),/unsafe/);
}));
for(const [key,value] of [['overflow_exit',0],['division_emitted_cil',true],['division_rejection_exit',124],['native_application_count',4],['outputs',[14,42,40,0]]])
  test('receipt rejects invalid arithmetic '+key,()=>fixture(({root,env,arithmetic,put})=>{
    arithmetic[key]=value;put('artifacts/evidence/linux-native-arithmetic.json',JSON.stringify(arithmetic));
    assert.throws(()=>createReceipt(root,env),/arithmetic/);
  }));
test('wrong head is denied',()=>fixture(({root,env})=>assert.throws(()=>createReceipt(root,{...env,EXPECTED_HEAD:'b'.repeat(40)}),/head mismatch/)));
test('modified tracked source is denied',()=>fixture(({root,env,put})=>{
  put('source.ores','modified');assert.throws(()=>createReceipt(root,env));
}));
test('untracked source is denied',()=>fixture(({root,env,put})=>{
  put('extra.ores','extra');assert.throws(()=>createReceipt(root,env),/untracked/);
}));
test('unapproved carrier is denied, even with a claimed source tree',()=>fixture(({root,env})=>{
  assert.throws(()=>createReceipt(root,{...env,GITHUB_REPOSITORY:'unapproved/repo'}),/source-local receipt/);
  assert.throws(()=>createReceipt(root,{...env,
    GITHUB_REPOSITORY:'honeypot-r-us-test/hnpt-e2e',
    EXPECTED_SOURCE_TREE:'c'.repeat(40)}),/external carrier not approved/);
}));
test('matching copied snapshot tree does not grant private-source export approval',()=>fixture(({root,env,put})=>{
  put('snapshot/source.ores','synthetic copy, not a private source export\\n');
  execFileSync('git',['-C',root,'add','snapshot']);
  execFileSync('git',['-C',root,'-c','user.name=Test','-c','user.email=test@local',
    'commit','-qm','Synthetic carrier tree']);
  const revision=execFileSync('git',['-C',root,'rev-parse','HEAD'],{encoding:'utf8'}).trim();
  const sourceTree=execFileSync('git',['-C',root,'rev-parse','HEAD:snapshot'],
    {encoding:'utf8'}).trim();
  assert.match(sourceTree,/^[a-f0-9]{40}$/);
  assert.throws(()=>createReceipt(root,{...env,EXPECTED_HEAD:revision,
    GITHUB_REPOSITORY:'honeypot-r-us-test/hnpt-e2e',
    EXPECTED_SOURCE_TREE:sourceTree}),/source-local receipt/);
}));
test('origin source receipt never substitutes external snapshot tree for the actual HEAD tree',
  ()=>fixture(({root,env})=>{
    const actualTree=execFileSync('git',['-C',root,'rev-parse','HEAD^{tree}'],
      {encoding:'utf8'}).trim();
    const receipt=createReceipt(root,{...env,EXPECTED_SOURCE_TREE:'c'.repeat(40)});
    assert.equal(receipt.tree_sha,actualTree);
    assert.equal(receipt.execution_repository,sourceRepository);
    assert.equal(receipt.execution_head_sha,env.EXPECTED_HEAD);
  }));
