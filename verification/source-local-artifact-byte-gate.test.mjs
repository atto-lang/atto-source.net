import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, writeFileSync, symlinkSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { verifyArtifactBytes } from './source-local-artifact-byte-gate.mjs';

const linuxFiles=['artifacts/compiler/linux-x64/oresnet','artifacts/apps/linux-x64/oreslang-app','artifacts/parity/linux-x64/oreslang-app','artifacts/parity-precedence/linux-x64/oreslang-app','artifacts/structured/linux-x64/oreslang-app'];
const windowsFiles=['artifacts/compiler/win-x64/oresnet.exe','artifacts/apps/win-x64/oreslang-app.exe'];
function elf(tag=0){const b=Buffer.alloc(512);b.set([0x7f,0x45,0x4c,0x46,2,1]);b.writeUInt16LE(3,16);b.writeUInt16LE(62,18);b.writeUInt32LE(tag,256);return b;}
function pe(tag=0){const b=Buffer.alloc(512);b.set([0x4d,0x5a]);b.writeUInt32LE(0x80,0x3c);b.set([0x50,0x45,0,0],0x80);b.writeUInt16LE(0x8664,0x84);b.writeUInt16LE(240,0x94);b.writeUInt16LE(0x20b,0x98);b.writeUInt32LE(tag,416);return b;}
const sha=b=>createHash('sha256').update(b).digest('hex');
function fixture(stripped=true) {
 const tmp=mkdtempSync(join(tmpdir(),'ores-artifact-test-'));
 const roots={linux:join(tmp,'linux'),windows:join(tmp,'windows')};
 mkdirSync(roots.linux);mkdirSync(roots.windows);
 const repo='ores-truffle-oreslang/oreslang-source.net';
 const source_head='a'.repeat(40),root_tree='b'.repeat(40),dispatchSha='9'.repeat(40);
 const flows={linux:['linux-x64-approved-source-local','trusted-source-local-linux-aot.yml',1001,'d'.repeat(40),['Authorize reviewed source before any checkout','Check out approved immutable source, not a PR workflow','Confirm exact Git commit, tree, and Native AOT prerequisites','Run source-free release gates and managed regressions','Publish native compiler and three isolated native applications','Publish structured CFG as fourth independent Native AOT executable','Record source tree and released executable SHA-256 identities','Upload bounded source-local Native AOT receipt']],windows:['self-hosted-windows-static-facts','trusted-source-local-windows-aot.yml',1002,'e'.repeat(40),['Preflight authorized immutable source without checkout','Checkout exact approved commit','Validate source head/tree before any compiler execution','Check installed Windows Native AOT toolchain','Run .NET compiler and runtime regression harness','Compile verified C# and F# source on trusted exact-head runner','Verify real scalar and experimental structured static facts','Publish and verify released Native AOT compiler','Execute real published Windows Native AOT application','Match Native AOT static facts against managed facts at exact head','Record immutable source and native Windows executable hashes','Upload immutable Windows Native AOT evidence']],fence:['source-local-windows-fence','trusted-source-local-fence-windows.yml',1003,'f'.repeat(40),['Authorize protected-main Windows fence snapshot before checkout','Checkout immutable approved Windows fence commit','Verify SHA and root tree before Windows fence build','Install .NET 10','Build isolated fence harness with warnings as errors','Execute 16 independent bounded race-stress rounds','Record verified source-bound fence test receipt','Upload Windows fence evidence']]};
 const r={approval:{repository:repo,source_head,root_tree,independent_review_verified:true,protected_main_verified:true,restricted_runner_group_verified:true,runner_group_id:345,workflows:{}},receipts:{},execution:{}};
 for(const [kind,[name,file,run_id,workflow_sha,required]] of Object.entries(flows)) {
   r.approval.workflows[kind]={workflow_sha,dispatch_head_sha:dispatchSha,run_id,attempt:1};
   r.execution[kind]={jobs_source:'repos/'+repo+'/actions/runs/'+run_id+'/attempts/1/jobs?per_page=100',run:{id:run_id,run_attempt:1,repository:{full_name:repo},event:'workflow_dispatch',head_branch:'main',head_sha:dispatchSha,status:'completed',conclusion:'success',path:'.github/workflows/'+file+'@main'},jobs:{total_count:1,jobs:[{name,run_id,runner_id:123,runner_group_id:345,runner_group_name:'oreslang-net-aot-trusted',head_sha:dispatchSha,head_branch:'main',status:'completed',conclusion:'success',steps:[{name:'Set up job',number:1,status:'completed',conclusion:'success'},...required.map((name,i)=>({name,number:i+2,status:'completed',conclusion:'success'})),{name:'Complete job',number:required.length+10,status:'completed',conclusion:'success'}]}]}};
 }
 const put=(kind,path,buf)=>{const dest=join(roots[kind],stripped?path.slice('artifacts/'.length):path);mkdirSync(dirname(dest),{recursive:true});writeFileSync(dest,buf);return dest;};
 const lh=linuxFiles.map((_,i)=>sha(elf(i+1))),wh=windowsFiles.map((_,i)=>sha(pe(i+1)));
 r.receipts.linux=['repository='+repo,'workflow_sha='+flows.linux[3],'workflow_ref='+repo+'/.github/workflows/'+flows.linux[1]+'@refs/heads/main','tested_source_head='+source_head,'tested_root_tree='+root_tree,'run_id=1001','run_attempt=1','target_rid=linux-x64',...linuxFiles.map((p,i)=>lh[i]+'  '+p)].join('\n')+'\n';
 r.receipts.windows={schema:'oreslang.net.source_local_windows_aot_v1',repository:repo,workflow_sha:flows.windows[3],workflow_ref:repo+'/.github/workflows/'+flows.windows[1]+'@refs/heads/main',tested_source_head:source_head,tested_root_tree:root_tree,run_id:'1002',run_attempt:'1',binaries:windowsFiles.map((path,i)=>({path,sha256:wh[i]}))};
 r.receipts.fence={schema:'oreslang.net.windows_fence_source_local_v1',repository:repo,workflow_sha:flows.fence[3],workflow_ref:repo+'/.github/workflows/'+flows.fence[1]+'@refs/heads/main',tested_source_head:source_head,tested_root_tree:root_tree,run_id:'1003',run_attempt:'1',rounds:16,test_marker:'ISOLATED_FENCE_TESTS_PASSED'};
 for(const [i,path] of linuxFiles.entries())put('linux',path,elf(i+1));
 for(const [i,path] of windowsFiles.entries())put('windows',path,pe(i+1));
 put('linux','artifacts/evidence/source-local-linux-sha256.txt',Buffer.from(r.receipts.linux));
 put('windows','artifacts/evidence/source-local-windows.json',Buffer.from(JSON.stringify(r.receipts.windows)));
 return {tmp,roots,bundle:r,put,linuxFiles,windowsFiles};
}
function scenario(name,mutate,pattern){test(name,async()=>{const f=fixture();try{mutate(f);await assert.rejects(verifyArtifactBytes(f.bundle,f.roots),pattern);}finally{rmSync(f.tmp,{recursive:true,force:true});}});}
for(const stripped of [true,false])test('accepts exact bytes and native format with stripped='+stripped,async()=>{
 const f=fixture(stripped);try{assert.deepEqual(await verifyArtifactBytes(f.bundle,f.roots),{linuxBinaries:5,windowsBinaries:2});}finally{rmSync(f.tmp,{recursive:true,force:true});}
});
scenario('rejects substituted Linux binary',f=>f.put('linux',f.linuxFiles[0],Buffer.concat([elf(),Buffer.from('changed')])),/SHA-256 mismatch/);
scenario('rejects missing Windows binary',f=>{rmSync(join(f.roots.windows,f.windowsFiles[1].slice(10)))},/inventory differs/);
scenario('rejects unexpected file',f=>f.put('linux','artifacts/secrets.txt',Buffer.from('secret')),/inventory differs/);
scenario('rejects mismatched receipt',f=>f.put('linux','artifacts/evidence/source-local-linux-sha256.txt',Buffer.from('changed')),/receipt differs/);
scenario('rejects managed CLR descriptor',f=>{const b=pe();b.writeUInt32LE(123,0x80+24+112+14*8);f.bundle.receipts.windows.binaries[0].sha256=sha(b);f.put('windows',f.windowsFiles[0],b);f.put('windows','artifacts/evidence/source-local-windows.json',Buffer.from(JSON.stringify(f.bundle.receipts.windows)));},/managed CLR descriptor/);
scenario('rejects false PE signature',f=>{const b=Buffer.alloc(512);f.bundle.receipts.windows.binaries[0].sha256=sha(b);f.put('windows',f.windowsFiles[0],b);f.put('windows','artifacts/evidence/source-local-windows.json',Buffer.from(JSON.stringify(f.bundle.receipts.windows)));},/not x86-64 native PE/);
scenario('rejects symlink substitution',f=>{const dest=join(f.roots.linux,f.linuxFiles[0].slice(10));rmSync(dest);symlinkSync(join(f.roots.linux,'evidence/source-local-linux-sha256.txt'),dest)},/symlink/);
scenario('rejects symlink extra entry',f=>{symlinkSync('/tmp',join(f.roots.linux,'unsafe'))},/symlink/);
scenario('rejects unapproved metadata',f=>{f.bundle.approval.independent_review_verified=false},/metadata admission refused/);

scenario('rejects duplicate SHA-256 across distinct Linux executables',f=>{
 const first=f.bundle.receipts.linux.match(/^([0-9a-f]{64})  artifacts\/compiler\/linux-x64\/oresnet/m)[1];
 f.bundle.receipts.linux=f.bundle.receipts.linux.replace(
  /^[0-9a-f]{64}(  artifacts\/apps\/linux-x64\/oreslang-app)$/m,first+'$1');
 f.put('linux','artifacts/evidence/source-local-linux-sha256.txt',Buffer.from(f.bundle.receipts.linux));
},/duplicate SHA-256/);
scenario('rejects duplicate SHA-256 across distinct Windows executables',f=>{
 f.bundle.receipts.windows.binaries[1].sha256=f.bundle.receipts.windows.binaries[0].sha256;
 f.put('windows','artifacts/evidence/source-local-windows.json',Buffer.from(JSON.stringify(f.bundle.receipts.windows)));
},/duplicate SHA-256/);
scenario('rejects oversized receipt payloads',f=>{
 f.put('windows','artifacts/evidence/source-local-windows.json',
   Buffer.from(JSON.stringify(f.bundle.receipts.windows)+' '.repeat(70000)));
},/oversized or invalid artifact receipt/);
