import { test } from "node:test";
import { readFileSync, readdirSync } from "node:fs";
import assert from "node:assert/strict";
import { verifyEvidence } from "./ci-evidence-gate.mjs";

const repo = "ores-truffle-oreslang/oreslang-source.net";
const sha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
const e = {repo,head:sha,runId:12345,attempt:1,workflow:"Windows Native AOT"};
const run = {id:12345,run_attempt:1,workflow_id:384715065,name:"Windows Native AOT",head_sha:sha,
  status:"completed",conclusion:"success",event:"pull_request",
  path:".github/workflows/windows-native-aot.yml",
  repository:{full_name:repo}};
const jobs = {total_count:2,jobs:[
  {name:"windows-x64",run_id:12345,run_attempt:1,head_sha:sha,
    status:"completed",conclusion:"success",steps:[
      "Assert exact originating-source checkout (Windows)",
      "Verify pinned Sendable Serializable DATA CONTROL contract",
      "Validate PowerShell verification syntax",
      "Show Windows .NET toolchain",
      "Refuse managed or framework-dependent release settings",
      "Verify compiler and runtime source",
      "Verify shared advisory static facts from .NET compiler and typed structured parser",
      "Reject oversized and invalid UTF-8 compiler input",
      "Publish native compiler and native application",
      "Exercise the released Native AOT compiler CLI",
      "Verify signed-i64 static facts on the published Native AOT compiler",
      "Adversarial scalar runner self-tests (Windows)",
      "Shared scalar source admission on the released Native AOT compiler",
      "Execute real Windows Native AOT binary",
      "Confirm strict Native AOT PE and reject managed assembly",
      "Reject corrupted Native AOT PE headers",
      "Execute Native AOT checked-arithmetic error cases",
      "Reject managed sidecar next to an otherwise native executable",
      "Compile verified CFG into a real Windows Native AOT executable",
      "Prove experimental structured source in real Windows Native AOT",
      "Execute typed source helper calls in real Windows Native AOT",
      "Execute typed statement-bodied helpers in real Windows Native AOT",
      "Verify typed bindings in executed Windows Native AOT",
      "Execute shared five-backend native.add source in real Windows Native AOT",
      "Execute shared native.precedence in Windows Native AOT",
      "Emit exact-head Windows Native AOT run receipt",
      "Upload exact-head Windows Native AOT receipt",
      "Upload native executable",
    ].map((name,i)=>({name,number:i+1,status:"completed",conclusion:"success"}))},
  {name:"linux-aot-diagnostic",run_id:12345,run_attempt:1,head_sha:sha,
    status:"completed",conclusion:"success",steps:[
      "Assert exact originating-source checkout (Linux)",
      "Install Linux Native AOT prerequisites",
      "Show Linux .NET toolchain",
      "Verify fail-closed AOT release settings",
      "Run frontend, metadata and mailbox regressions",
      "Validate real .NET static-facts JSON on Linux managed compiler",
      "Adversarial scalar runner self-tests (Linux)",
      "Shared scalar source admission diagnostic (managed compiler only)",
      "Emit generated CIL using the compiler",
      "Native AOT publish generated application (diagnostic only)",
      "Execute Linux Native AOT generated application",
      "Execute Linux Native AOT CFG branch and simultaneous-assignment fixture",
      "Prove experimental structured source in real Linux Native AOT",
      "Execute typed source helper calls in real Linux Native AOT",
      "Execute typed statement-bodied helpers in real Linux Native AOT",
      "Verify typed bindings in executed Linux Native AOT",
    ].map((name,i)=>({name,number:i+1,status:"completed",conclusion:"success"}))},
]};
const copy=x=>JSON.parse(JSON.stringify(x));
const check=(r,j)=>verifyEvidence(r,j,e);
test("valid exact-head, same-run, full-step evidence",()=>assert.deepEqual(check(run,jobs),[]));
test("zero steps are never proof",()=>{const j=copy(jobs);j.jobs[0].steps=[];assert.match(check(run,j).join(" "),/zero executed/);});
test("different repository rejected",()=>assert.match(check({...run,repository:{full_name:"other/repo"}},jobs).join(" "),/repository/));
test("same-name workflow at wrong path rejected",()=>assert.match(
  check({...run,path:".github/workflows/spoof-windows-native-aot.yml"},jobs).join(" "),
  /workflow path/));
test("same-name and path but wrong workflow ID rejected",()=>assert.match(
  check({...run,workflow_id:388530125},jobs).join(" "), /workflow ID/));
test("missing workflow ID rejected",()=>{
  const r=copy(run);delete r.workflow_id;
  assert.match(check(r,jobs).join(" "), /workflow ID/);
});
// Current main requires additional executable typed-source proof on both OSes.
// No green run may omit any of these substantive Native AOT stages.
for (const [jobIndex, stepName] of [[0,"Execute typed source helper calls in real Windows Native AOT"],[0,"Execute typed statement-bodied helpers in real Windows Native AOT"],[0,"Verify typed bindings in executed Windows Native AOT"],[1,"Execute typed source helper calls in real Linux Native AOT"],[1,"Execute typed statement-bodied helpers in real Linux Native AOT"],[1,"Verify typed bindings in executed Linux Native AOT"]]) {
  test(`new Native AOT stage is mandatory: ${stepName}`, () => {
    const j=copy(jobs);
    j.jobs[jobIndex].steps=j.jobs[jobIndex].steps.filter(s=>s.name!==stepName);
    assert.match(check(run,j).join(" "), /missing successful step/);
  });
}

test("wrong SHA rejected",()=>assert.match(check({...run,head_sha:"0".repeat(40)},jobs).join(" "),/head/));
test("wrong attempt rejected",()=>assert.match(check({...run,run_attempt:2},jobs).join(" "),/attempt/));
test("wrong run rejected",()=>assert.match(check({...run,id:44},jobs).join(" "),/run ID/));
test("failed step rejected",()=>{const j=copy(jobs);j.jobs[0].steps[1].conclusion="failure";assert.notEqual(check(run,j).length,0);});
test("missing mandatory job rejected",()=>{const j=copy(jobs);j.jobs.pop();j.total_count=1;assert.match(check(run,j).join(" "),/missing/);});
test("paginated/truncated jobs rejected",()=>{const j=copy(jobs);j.total_count=3;assert.match(check(run,j).join(" "),/incomplete/);});
test("pending workflow rejected",()=>assert.match(check({...run,status:"in_progress",conclusion:null},jobs).join(" "),/not passed/));
test("missing native execution step rejected",()=>{const j=copy(jobs);j.jobs[0].steps = j.jobs[0].steps.filter(s=>s.name!=="Execute real Windows Native AOT binary");assert.match(check(run,j).join(" "),/missing successful step/);});
test("ambiguous duplicate job rejected",()=>{const j=copy(jobs);j.jobs.push(copy(j.jobs[0]));j.total_count=3;assert.match(check(run,j).join(" "),/duplicated/);});
test("declaration-only evidence must not masquerade as AOT",()=>{
  const x=copy(run);x.name="Integration declaration integrity";x.path=".github/workflows/integration-declarations.yml";x.workflow_id=388530125;
  const p={...e,workflow:"Integration declaration integrity"};
  assert.notEqual(verifyEvidence(x,jobs,p).length,0);
});

test("checkout assertion is required",()=>{const j=copy(jobs);j.jobs[0].steps.shift();assert.match(check(run,j).join(" "),/missing successful step/);});
test("receipt upload is required",()=>{const j=copy(jobs);j.jobs[0].steps.pop();assert.match(check(run,j).join(" "),/missing successful step/);});
test("disabled self-hosted fallback is permitted only alongside mandatory hosted jobs",()=>{
  const j=copy(jobs);j.jobs.push({name:"self-hosted-windows-static-facts",run_id:12345,run_attempt:1,head_sha:sha,status:"completed",conclusion:"skipped"});j.total_count++;
  assert.deepEqual(check(run,j),[]);
  j.jobs.shift();j.total_count--;assert.match(check(run,j).join(" "),/missing/);
});
test("failed opt-in self-hosted fallback remains rejected",()=>{
  const j=copy(jobs);j.jobs.push({name:"self-hosted-windows-static-facts",run_id:12345,run_attempt:1,head_sha:sha,status:"completed",conclusion:"failure"});j.total_count++;
  assert.match(check(run,j).join(" "),/not successful/);
});
test("Rust declaration tooling evidence is admitted and missing Rust tests rejected",()=>{
  const p={...e,workflow:"Integration declaration integrity"};const r={...run,name:p.workflow,path:".github/workflows/integration-declarations.yml",workflow_id:388530125};
  const j={total_count:1,jobs:[{name:"declaration-integrity",run_id:e.runId,run_attempt:1,head_sha:sha,status:"completed",conclusion:"success",steps:[
    "Assert exact originating-source checkout","Test Rust repository policy tooling",
    "Verify declaration-only manifest (not a runtime parity claim)"
  ].map((name,i)=>({name,number:i+1,status:"completed",conclusion:"success"}))}]};
  assert.deepEqual(verifyEvidence(r,j,p),[]);j.jobs[0].steps.splice(1,1);
  assert.match(verifyEvidence(r,j,p).join(" "),/missing successful step/);
});

for (const name of ["Publish native compiler and native application",
  "Verify signed-i64 static facts on the published Native AOT compiler",
  "Reject corrupted Native AOT PE headers",
  "Compile verified CFG into a real Windows Native AOT executable"]) {
  test(`omitted substantive Windows step denied: ${name}`, () => {
    const j=copy(jobs);j.jobs[0].steps=j.jobs[0].steps.filter(s=>s.name!==name);
    assert.match(check(run,j).join(" "), /missing successful step/);
  });
}
test("omitted substantive Linux CFG step denied",()=>{
  const j=copy(jobs);j.jobs[1].steps=j.jobs[1].steps.filter(s=>s.name!==
    "Execute Linux Native AOT CFG branch and simultaneous-assignment fixture");
  assert.match(check(run,j).join(" "),/missing successful step/);
});
for (const [index, name] of [
  [0, "Prove experimental structured source in real Windows Native AOT"],
  [1, "Prove experimental structured source in real Linux Native AOT"]
]) {
  test(`missing experimental source Native AOT stage rejected: ${name}`, () => {
    const j=copy(jobs);
    j.jobs[index].steps=j.jobs[index].steps.filter(s=>s.name!==name);
    assert.match(check(run,j).join(" "), /missing successful step/);
  });
}
test("duplicate step names are ambiguous even with different numbers",()=>{
  const j=copy(jobs);j.jobs[0].steps.push({...j.jobs[0].steps[0],number:100});
  assert.match(check(run,j).join(" "),/duplicate step name/);
});
test("invalid and duplicate step numbers are denied",()=>{
  for (const number of [0,-1,1,1.5]) {
    const j=copy(jobs);j.jobs[0].steps[1].number=number;
    assert.match(check(run,j).join(" "),/malformed step|duplicate step number/);
  }
});
test("malformed job rows cannot hide inside a complete job count",()=>{
  const j=copy(jobs);j.jobs.push(null);j.total_count++;
  assert.match(check(run,j).join(" "),/malformed job/);
});
test("optional skipped job still requires same run, attempt and source identity",()=>{
  for (const override of [{run_id:1},{run_attempt:2},{head_sha:"0".repeat(40)}]) {
    const j=copy(jobs);j.jobs.push({name:"self-hosted-windows-static-facts",
      run_id:12345,run_attempt:1,head_sha:sha,status:"completed",conclusion:"skipped",...override});
    j.total_count++;assert.notEqual(check(run,j).length,0);
  }
});
test("missing job attempt cannot stand for independently selected attempt",()=>{
  const j=copy(jobs);delete j.jobs[0].run_attempt;
  assert.match(check(run,j).join(" "),/different attempt/);
});

test("hosted evidence fixtures cover every explicitly named source workflow step",()=>{
  const source=readFileSync(new URL("../.github/workflows/windows-native-aot.yml",import.meta.url),"utf8");
  for(const job of jobs.jobs) {
    const block=source.split(`  ${job.name}:\n`)[1]?.split(/^  [a-z][a-z0-9-]*:/m)[0];
    assert.ok(block,`source workflow job ${job.name} exists`);
    const names=[...block.matchAll(/^      - name: (.+)$/gm)].map(m=>m[1]);
    assert.deepEqual(job.steps.map(s=>s.name),names);
    for(const name of names) {
      const j=copy(jobs);const target=j.jobs.find(item=>item.name===job.name);
      target.steps=target.steps.filter(step=>step.name!==name);
      assert.match(check(run,j).join(" "),/missing successful step/,`${job.name}: ${name}`);
    }
  }
});

test('Windows and Linux scalar provenance reports use exact checkout head, not synthetic PR merge SHA', () => {
  const source=readFileSync(new URL("../.github/workflows/windows-native-aot.yml",import.meta.url),"utf8");
  const exact="--revision '${{ github.event.pull_request.head.sha || github.sha }}'";
  const synthetic="--revision '${{ github.sha }}'";
  const windows=source.split("  windows-x64:\n")[1]?.split("  linux-aot-diagnostic:\n")[0];
  const linux=source.split("  linux-aot-diagnostic:\n")[1]?.split("  self-hosted-windows-static-facts:\n")[0];
  assert.ok(windows && linux,"both verified Windows and Linux jobs must exist");
  for(const [label,job] of [["Windows",windows],["Linux",linux]]) {
    assert.ok(job.includes("ref: ${{ github.event.pull_request.head.sha || github.sha }}"),label+" checkout must target PR head");
    assert.equal(job.split(exact).length-1,1,label+" report must declare the exact PR head exactly once");
    assert.ok(!job.includes(synthetic),label+" must not report synthetic merge SHA");
  }
});

test('evidence-parser workflow itself tests the exact PR head on Windows workflow changes', () => {
  const source=readFileSync(new URL("../.github/workflows/native-aot-evidence-contract.yml",import.meta.url),"utf8");
  assert.ok(source.includes("      - '.github/workflows/windows-native-aot.yml'"));
  assert.ok(source.includes("ref: ${{ github.event.pull_request.head.sha || github.sha }}"));
  assert.ok(source.includes("EXPECTED_HEAD: ${{ github.event.pull_request.head.sha || github.sha }}"));
  assert.ok(source.includes("git rev-parse HEAD"));
  assert.ok(source.includes("git diff --quiet --exit-code HEAD --"));
  assert.ok(source.includes("node --test verification/ci-evidence-gate.test.mjs"));
});

test('Native AOT structured lane rejects shipping compile and cannot pass due to missing output directories',()=>{
  const source=readFileSync(new URL("../.github/workflows/windows-native-aot.yml",import.meta.url),"utf8");
  const windows=source.split("      - name: Prove experimental structured source in real Windows Native AOT\n")[1]
    ?.split("      - name: Execute shared five-backend native.add source in real Windows Native AOT\n")[0];
  const linux=source.split("      - name: Prove experimental structured source in real Linux Native AOT\n")[1]
    ?.split("  # Optional budget-independent gate.")[0];
  assert.ok(windows && linux,"both platform structured native jobs must exist");
  const ordered=(block,parts,label)=>{
    let prev=-1;
    for(const part of parts){
      const index=block.indexOf(part);
      assert.ok(index>prev,label+" requires ordered stage: "+part);
      prev=index;
    }
  };
  ordered(windows,[
    "& $compiler check $source",
    "$shipping = Join-Path $folder 'shipping/OresGenerated.dll'",
    "New-Item -ItemType Directory -Path (Split-Path -Parent $shipping) -Force",
    "& $compiler compile $source --out $shipping",
    "if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $shipping))",
    "& $compiler check-structured $source",
    "& $compiler compile-structured $source --out $generated",
    "$forbidden = Join-Path $folder 'denied/OresGenerated.dll'",
    "New-Item -ItemType Directory -Path (Split-Path -Parent $forbidden) -Force",
    "& $compiler compile-structured $unsupported --out $forbidden"
  ],"Windows");
  ordered(linux,[
    'if "$compiler" check "$source_file"; then',
    'shipping="$GITHUB_WORKSPACE/artifacts/structured-generated/shipping/OresGenerated.dll"',
    'mkdir -p "$(dirname "$shipping")"',
    'if "$compiler" compile "$source_file" --out "$shipping"; then',
    'test ! -e "$shipping"',
    '"$compiler" check-structured "$source_file"',
    '"$compiler" compile-structured "$source_file" --out "$generated"',
    'forbidden="$GITHUB_WORKSPACE/artifacts/structured-generated/denied/OresGenerated.dll"',
    'mkdir -p "$(dirname "$forbidden")"',
    'if "$compiler" compile-structured "$RUNNER_TEMP/unsupported-select.ores" --out "$forbidden"; then'
  ],"Linux");
});

test('Native AOT CI uses immutable action revisions, never mutable tags',()=>{
  const files=[
    "../.github/workflows/windows-native-aot.yml",
    "../.github/workflows/native-aot-evidence-contract.yml"
  ];
  const references=source=>[...source.matchAll(/^\s*-?\s*uses:\s+([^\s#]+)/gm)]
    .map(match=>match[1]).filter(ref=>!ref.startsWith("./"));
  const isPinned=ref=>/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+@[a-f0-9]{40}$/i.test(ref);
  for(const path of files){
    const source=readFileSync(new URL(path,import.meta.url),"utf8");
    const refs=references(source);
    assert.ok(refs.length>=1,path+" has external workflow actions");
    for(const ref of refs)assert.ok(isPinned(ref),path+" unpinned: "+ref);
  }
  const source=readFileSync(new URL(files[0],import.meta.url),"utf8");
  assert.ok(references(source).includes("actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02"),
    "native executable upload must be pinned to audited upload-artifact revision");
  const tampered=source.replace("actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02",
    "actions/upload-artifact@v4");
  assert.ok(references(tampered).some(ref=>!isPinned(ref)),
    "immutable action policy must detect an adversarial mutable-tag edit");
});

test('all CI workflow actions use immutable 40-hex commit SHAs',()=>{
  const directory=new URL("../.github/workflows/",import.meta.url);
  const names=readdirSync(directory).filter(name=>/\.ya?ml$/.test(name));
  assert.ok(names.length>=14,"expected complete current workflow inventory");
  const refs=source=>[...source.matchAll(/^\s*-?\s*uses:\s+([^\s#]+)/gm)]
    .map(m=>m[1]).filter(ref=>!ref.startsWith("./"));
  const pinned=ref=>/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+@[a-f0-9]{40}$/i.test(ref);
  let count=0;
  for(const name of names){
    const source=readFileSync(new URL("../.github/workflows/"+name,import.meta.url),"utf8");
    for(const ref of refs(source)){
      count++;
      assert.ok(pinned(ref),name+" uses an unpinned or malformed action "+ref);
    }
  }
  assert.ok(count>=25,"workflow action inventory incomplete");
  assert.ok(!pinned("actions/checkout@11bd71901bbe5b1630ceea73d27597364c9a"),
    "truncated revision must fail the full-length pin validator");
});

test('DOP CPU region CI checks out exact source and does not persist credentials',()=>{
  const source=readFileSync(new URL("../.github/workflows/dop-cpu-region-contract.yml",import.meta.url),"utf8");
  assert.ok(source.includes("ref: ${{ github.event.pull_request.head.sha || github.sha }}"));
  assert.ok(source.includes("persist-credentials: false"));
  assert.ok(source.includes("EXPECTED_HEAD: ${{ github.event.pull_request.head.sha || github.sha }}"));
  assert.ok(source.includes("git rev-parse HEAD"));
  assert.ok(source.includes("git diff --quiet --exit-code HEAD --"));
  assert.ok(source.includes("git ls-files --others --exclude-standard"));
});
