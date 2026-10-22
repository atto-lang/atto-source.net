import { test } from "node:test";
import assert from "node:assert/strict";
import { verifyEvidence } from "./ci-evidence-gate.mjs";

const repo = "ores-truffle-oreslang/oreslang-source.net";
const sha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
const e = {repo,head:sha,runId:12345,attempt:1,workflow:"Windows Native AOT"};
const run = {id:12345,run_attempt:1,name:"Windows Native AOT",head_sha:sha,
  status:"completed",conclusion:"success",event:"pull_request",
  repository:{full_name:repo}};
const jobs = {total_count:2,jobs:[
  {name:"windows-x64",run_id:12345,status:"completed",conclusion:"success",steps:[
    "Verify compiler and runtime source",
    "Execute real Windows Native AOT binary",
    "Confirm strict Native AOT PE and reject managed assembly"
  ].map((name,i)=>({name,number:i+1,status:"completed",conclusion:"success"}))},
  {name:"linux-aot-diagnostic",run_id:12345,status:"completed",conclusion:"success",steps:[
    "Run frontend, metadata and mailbox regressions",
    "Native AOT publish generated application (diagnostic only)",
    "Execute Linux Native AOT generated application"
  ].map((name,i)=>({name,number:i+1,status:"completed",conclusion:"success"}))}
]};
const copy=x=>JSON.parse(JSON.stringify(x));
const check=(r,j)=>verifyEvidence(r,j,e);
test("valid exact-head, same-run, full-step evidence",()=>assert.deepEqual(check(run,jobs),[]));
test("zero steps are never proof",()=>{const j=copy(jobs);j.jobs[0].steps=[];assert.match(check(run,j).join(" "),/zero executed/);});
test("different repository rejected",()=>assert.match(check({...run,repository:{full_name:"other/repo"}},jobs).join(" "),/repository/));
test("wrong SHA rejected",()=>assert.match(check({...run,head_sha:"0".repeat(40)},jobs).join(" "),/head/));
test("wrong attempt rejected",()=>assert.match(check({...run,run_attempt:2},jobs).join(" "),/attempt/));
test("wrong run rejected",()=>assert.match(check({...run,id:44},jobs).join(" "),/run ID/));
test("failed step rejected",()=>{const j=copy(jobs);j.jobs[0].steps[1].conclusion="failure";assert.notEqual(check(run,j).length,0);});
test("missing mandatory job rejected",()=>{const j=copy(jobs);j.jobs.pop();j.total_count=1;assert.match(check(run,j).join(" "),/missing/);});
test("paginated/truncated jobs rejected",()=>{const j=copy(jobs);j.total_count=3;assert.match(check(run,j).join(" "),/incomplete/);});
test("pending workflow rejected",()=>assert.match(check({...run,status:"in_progress",conclusion:null},jobs).join(" "),/not passed/));
test("missing native execution step rejected",()=>{const j=copy(jobs);j.jobs[0].steps.splice(1,1);assert.match(check(run,j).join(" "),/missing successful step/);});
test("ambiguous duplicate job rejected",()=>{const j=copy(jobs);j.jobs.push(copy(j.jobs[0]));j.total_count=3;assert.match(check(run,j).join(" "),/duplicated/);});
test("declaration-only evidence must not masquerade as AOT",()=>{
  const x=copy(run);x.name="Integration declaration integrity";
  const p={...e,workflow:"Integration declaration integrity"};
  assert.notEqual(verifyEvidence(x,jobs,p).length,0);
});
