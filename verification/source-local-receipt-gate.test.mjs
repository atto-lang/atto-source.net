import { test } from 'node:test';
import assert from 'node:assert/strict';
import { verifySourceLocalBundle } from './source-local-receipt-gate.mjs';

const repo = 'ores-truffle-oreslang/oreslang-source.net';
const source_head = 'a'.repeat(40), root_tree = 'b'.repeat(40);
const hash = 'c'.repeat(64);
const steps = {
  linux: ['Authorize reviewed source before any checkout','Check out approved immutable source, not a PR workflow','Confirm exact Git commit, tree, and Native AOT prerequisites','Run source-free release gates and managed regressions','Publish native compiler and three isolated native applications','Publish structured CFG as fourth independent Native AOT executable','Record source tree and released executable SHA-256 identities','Upload bounded source-local Native AOT receipt'],
  windows: ['Preflight authorized immutable source without checkout','Checkout exact approved commit','Validate source head/tree before any compiler execution','Check installed Windows Native AOT toolchain','Run .NET compiler and runtime regression harness','Compile verified C# and F# source on trusted exact-head runner','Verify real scalar and experimental structured static facts','Publish and verify released Native AOT compiler','Execute real published Windows Native AOT application','Match Native AOT static facts against managed facts at exact head','Record immutable source and native Windows executable hashes','Upload immutable Windows Native AOT evidence'],
  fence: ['Authorize protected-main Windows fence snapshot before checkout','Checkout immutable approved Windows fence commit','Verify SHA and root tree before Windows fence build','Install .NET 10','Build isolated fence harness with warnings as errors','Execute 16 independent bounded race-stress rounds','Record verified source-bound fence test receipt','Upload Windows fence evidence'],
};
const path = { linux:'trusted-source-local-linux-aot.yml',windows:'trusted-source-local-windows-aot.yml',fence:'trusted-source-local-fence-windows.yml' };
const jobName = { linux:'linux-x64-approved-source-local',windows:'self-hosted-windows-static-facts',fence:'source-local-windows-fence' };
const linuxPaths = ['artifacts/compiler/linux-x64/oresnet','artifacts/apps/linux-x64/oreslang-app','artifacts/parity/linux-x64/oreslang-app','artifacts/parity-precedence/linux-x64/oreslang-app','artifacts/structured/linux-x64/oreslang-app'];
const expectedRun = { linux:1001, windows:1002, fence:1003 };
const workflowSha = { linux:'d'.repeat(40), windows:'e'.repeat(40), fence:'f'.repeat(40) };
// These model independent GitHub run and workflow-file revisions.
const dispatchSha = '9'.repeat(40);
const ref = kind => repo+'/.github/workflows/'+path[kind]+'@refs/heads/main';
const jobsSource = (kind,run_id,attempt=1) => 'repos/'+repo+'/actions/runs/'+run_id+'/attempts/'+attempt+'/jobs?per_page=100';
function fixture() {
  const approval = {repository:repo,source_head,root_tree,
    independent_review_verified:true,protected_main_verified:true,restricted_runner_group_verified:true,runner_group_id:345,
    workflows:{}};
  const receipts = {}, execution = {};
  for(const kind of Object.keys(steps)) {
    const run_id = expectedRun[kind];
    approval.workflows[kind] = {workflow_sha:workflowSha[kind],dispatch_head_sha:dispatchSha,run_id,attempt:1};
    execution[kind] = {
      jobs_source:jobsSource(kind,run_id),
      run:{id:run_id,run_attempt:1,repository:{full_name:repo},event:'workflow_dispatch',
        head_branch:'main',head_sha:dispatchSha,status:'completed',conclusion:'success',
        path:'.github/workflows/'+path[kind]+'@main'},
      jobs:{total_count:1,jobs:[{name:jobName[kind],run_id,runner_id:123,
        runner_group_id:345,runner_group_name:'oreslang-net-aot-trusted',
        head_sha:dispatchSha,head_branch:'main',
        status:'completed',conclusion:'success',steps:[
          {name:'Set up job',number:1,status:'completed',conclusion:'success'},
          ...steps[kind].map((name,i)=>({name,number:i+2,status:'completed',conclusion:'success'})),
          {name:'Post actions/checkout',number:steps[kind].length+9,status:'completed',conclusion:'success'},
          {name:'Complete job',number:steps[kind].length+10,status:'completed',conclusion:'success'}
        ]}]}
    };
    if(kind==='linux') receipts[kind]=[
      'repository='+repo,'workflow_sha='+workflowSha[kind],'workflow_ref='+ref(kind),
      'tested_source_head='+source_head,'tested_root_tree='+root_tree,
      'run_id='+run_id,'run_attempt=1','target_rid=linux-x64',
      ...linuxPaths.map(path=>hash+'  '+path)
    ].join('\n')+'\n';
    else receipts[kind] = {
      schema:kind==='windows'?'oreslang.net.source_local_windows_aot_v1':'oreslang.net.windows_fence_source_local_v1',
      repository:repo,workflow_sha:workflowSha[kind],workflow_ref:ref(kind),
      tested_source_head:source_head,tested_root_tree:root_tree,run_id:String(run_id),run_attempt:'1',
      ...(kind==='windows'?{binaries:[
        {path:'artifacts/compiler/win-x64/oresnet.exe',sha256:hash},
        {path:'artifacts/apps/win-x64/oreslang-app.exe',sha256:hash}
      ]}:{rounds:16,test_marker:'ISOLATED_FENCE_TESTS_PASSED'})
    };
  }
  return {approval,receipts,execution};
}
function deny(label,mutate,pattern){test(label,()=>{const data=fixture();mutate(data);assert.match(verifySourceLocalBundle(data).join('; '),pattern);});}
test('source-free synthetic happy path is metadata-consistent, not certified',()=>assert.deepEqual(verifySourceLocalBundle(fixture()),[]));
deny('refuses absent independent review', x=>x.approval.independent_review_verified=false,/independent review/);
deny('refuses unprotected main',x=>x.approval.protected_main_verified=false,/protected main/);
deny('refuses unverified runner group',x=>x.approval.restricted_runner_group_verified=false,/runner group/);
deny('refuses cross-org run',x=>x.execution.linux.run.repository.full_name='another/repo',/source-local manual workflow/);
deny('refuses PR execution',x=>x.execution.windows.run.event='pull_request',/source-local manual workflow/);
deny('refuses wrong main dispatch SHA',x=>x.execution.fence.run.head_sha='0'.repeat(40),/approved main dispatch head/);
deny('refuses unapproved dispatch SHA',x=>x.approval.workflows.linux.dispatch_head_sha='',/invalid approved dispatch head SHA/);
deny('refuses receipt workflow-SHA confusion',x=>x.receipts.windows.workflow_sha=dispatchSha,/receipt workflow identity mismatch/);
deny('refuses unassigned runner',x=>x.execution.windows.jobs.jobs[0].runner_id=0,/real allocated runner/);
deny('refuses skipped native stage',x=>x.execution.linux.jobs.jobs[0].steps[4].conclusion='skipped',/unsuccessful execution step/);
deny('refuses omitted stage',x=>x.execution.windows.jobs.jobs[0].steps.splice(3,1),/required steps missing/);
deny('refuses wrong source tree in receipt',x=>x.receipts.fence.tested_root_tree='0'.repeat(40),/root tree mismatch/);
deny('refuses wrong workflow ref in receipt',x=>x.receipts.windows.workflow_ref='unsafe',/workflow identity mismatch/);
deny('refuses duplicate Linux receipt metadata',x=>x.receipts.linux+='run_id=1001\n',/duplicate linux receipt field/);
deny('refuses missing Linux native binary',x=>x.receipts.linux=x.receipts.linux.replace(/^[a-f0-9]{64}  artifacts\/structured\/linux-x64\/oreslang-app\n/m,''),/linux binaries incomplete/);
deny('refuses duplicate Windows binary path',x=>x.receipts.windows.binaries[1].path=x.receipts.windows.binaries[0].path,/duplicate windows binary/);
deny('refuses unsupported Windows binary digest',x=>x.receipts.windows.binaries[0].sha256='bad',/binary path or digest/);
deny('refuses insufficient fence rounds',x=>x.receipts.fence.rounds=15,/16 successful stress rounds/);
deny('refuses duplicate run IDs',x=>x.approval.workflows.fence.run_id=x.approval.workflows.windows.run_id,/duplicate run ID/);
deny('refuses non-successful run',x=>x.execution.linux.run.conclusion='failure',/run not successful/);
deny('refuses zero-step job',x=>x.execution.windows.jobs.jobs[0].steps=[],/insufficient executed steps/);
deny('refuses wrong workflow-run branch suffix',x=>x.execution.linux.run.path='.github/workflows/'+path.linux+'@feature/unsafe',/unapproved ref/);
deny('refuses PR ref in run path',x=>x.execution.windows.run.path='.github/workflows/'+path.windows+'@refs/pull/214/merge',/unapproved ref/);
deny('refuses bare workflow-run path',x=>x.execution.fence.run.path='.github/workflows/'+path.fence,/unapproved ref/);
deny('refuses wrong attempt-scoped jobs endpoint',x=>x.execution.linux.jobs_source=jobsSource('linux',1001,2),/attempt-specific jobs endpoint/);
deny('refuses wrong runner-group ID',x=>x.execution.windows.jobs.jobs[0].runner_group_id=9,/restricted runner group/);
deny('refuses wrong runner-group name',x=>x.execution.windows.jobs.jobs[0].runner_group_name='default',/restricted runner group/);
deny('refuses missing independently approved runner group ID',x=>x.approval.runner_group_id=0,/approved runner group ID/);
deny('refuses wrong job source SHA',x=>x.execution.fence.jobs.jobs[0].head_sha='0'.repeat(40),/job source revision/);
deny('refuses wrong job attempt if present',x=>x.execution.fence.jobs.jobs[0].run_attempt=2,/job identity mismatch/);
deny('refuses renamed mandatory step',x=>x.execution.linux.jobs.jobs[0].steps[2].name='renamed',/required steps missing/);
deny('refuses duplicate mandatory step',x=>x.execution.linux.jobs.jobs[0].steps[0].name=steps.linux[0],/duplicate required step/);
deny('refuses reordered mandatory steps',x=>{
  const a=x.execution.linux.jobs.jobs[0].steps; [a[2].name,a[3].name]=[a[3].name,a[2].name];
},/required steps out of order/);
deny('refuses failed runner-owned step',x=>x.execution.windows.jobs.jobs[0].steps[0].conclusion='failure',/unsuccessful execution step/);
deny('refuses nonmonotonic step number',x=>x.execution.windows.jobs.jobs[0].steps[2].number=2,/non-increasing/);
test('accepts refs heads main and matching optional job attempt',()=>{
 const x=fixture();x.execution.linux.run.path='.github/workflows/'+path.linux+'@refs/heads/main';
 x.execution.linux.jobs.jobs[0].run_attempt=1;
 assert.deepEqual(verifySourceLocalBundle(x),[]);
});
