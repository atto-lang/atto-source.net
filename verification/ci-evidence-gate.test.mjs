import { test } from "node:test";
import { readFileSync, readdirSync } from "node:fs";
import assert from "node:assert/strict";
import { verifyEvidence } from "./ci-evidence-gate.mjs";

const repo = "ores-truffle-oreslang/oreslang-source.net";
const sha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
const e = {repo,head:sha,runId:12345,attempt:1,workflow:"Windows Native AOT"};
const run = {id:12345,run_attempt:1,name:"Windows Native AOT",head_sha:sha,
  status:"completed",conclusion:"success",event:"pull_request",
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
      "Compile verified scalar OresIR through C# and F# to managed CIL",
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
  const x=copy(run);x.name="Integration declaration integrity";
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
  const p={...e,workflow:"Integration declaration integrity"};const r={...run,name:p.workflow};
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


test("Windows C# and F# managed-source backend execution is mandatory evidence",()=>{
  const changed=copy(jobs);
  changed.jobs[0].steps=changed.jobs[0].steps.filter(s=>
    s.name!=="Compile verified scalar OresIR through C# and F# to managed CIL");
  assert.match(check(run,changed).join(" "),/missing successful step/);
});

test("typed Windows and Linux Native AOT execution stages cannot be omitted",()=>{
  for(const [jobIndex,labels] of [
    [0,["Execute typed source helper calls in real Windows Native AOT",
        "Execute typed statement-bodied helpers in real Windows Native AOT",
        "Verify typed bindings in executed Windows Native AOT"]],
    [1,["Execute typed source helper calls in real Linux Native AOT",
        "Execute typed statement-bodied helpers in real Linux Native AOT",
        "Verify typed bindings in executed Linux Native AOT"]]
  ]) {
    for(const label of labels) {
      const changed=copy(jobs);
      changed.jobs[jobIndex].steps=changed.jobs[jobIndex].steps.filter(s=>s.name!==label);
      assert.match(check(run,changed).join(" "),/missing successful step/,label);
    }
  }
});

const linuxWorkflow = "Strict .NET Linux x64 Native AOT";
const linuxExpected = {...e,workflow:linuxWorkflow};
const linuxSource = readFileSync(new URL("../.github/workflows/strict-linux-native-aot.yml",import.meta.url),"utf8");
const linuxHosted = linuxSource.split("  linux-x64-compiler-and-app:\n")[1]?.split("  # TRUST BOUNDARY")[0];
const linuxSteps = [...linuxHosted.matchAll(/^      - name: (.+)$/gm)].map(x=>x[1]);
const linuxRun = {...run,name:linuxWorkflow};
const linuxJobs = {total_count:2,jobs:[
  {name:"linux-x64-compiler-and-app",run_id:e.runId,run_attempt:e.attempt,head_sha:sha,
    status:"completed",conclusion:"success",steps:linuxSteps.map((name,i)=>({
      name,number:i+1,status:"completed",conclusion:"success"
    }))},
  {name:"linux-x64-approved-source-local",run_id:e.runId,run_attempt:e.attempt,head_sha:sha,
    status:"completed",conclusion:"skipped"}
]};
const linuxVerify = (r,j)=>verifyEvidence(r,j,linuxExpected);
test("manual source-local dispatch skips hosted CI and requires protected workflow authority",()=> {
  const hosted=linuxSource.split("  linux-x64-compiler-and-app:\n")[1]
    ?.split("  linux-x64-approved-source-local:\n")[0];
  const approved=linuxSource.split("  linux-x64-approved-source-local:\n")[1];
  assert.ok(hosted && approved,"both Linux jobs must exist");
  assert.match(hosted,/^    if: github\.event_name != 'workflow_dispatch'$/m);
  assert.ok(approved.includes("github.event_name == 'workflow_dispatch'"));
  assert.ok(approved.includes("github.ref_protected == true"));
  assert.ok(approved.includes("github.repository_id == '1419704375'"));
  assert.ok(approved.includes("github.workflow_ref == 'ores-truffle-oreslang/oreslang-source.net/.github/workflows/strict-linux-native-aot.yml@refs/heads/main'"));
  assert.ok(approved.includes("group: oreslang-net-aot-trusted"));
  assert.ok(approved.includes("persist-credentials: false"));
});

test("strict Linux exact-head hosted Native AOT passes only with all named executed stages",()=>{
  assert.equal(linuxSteps.length,10,"expected the exact ten source-owned Linux release stages");
  assert.deepEqual(linuxVerify(linuxRun,linuxJobs),[]);
});
test("strict Linux zero-step runner failure is not certification",()=>{
  const j=copy(linuxJobs);j.jobs[0].steps=[];
  assert.match(linuxVerify(linuxRun,j).join(" "),/zero executed steps/);
});
test("strict Linux native execution stage cannot be omitted",()=>{
  const j=copy(linuxJobs);
  j.jobs[0].steps=j.jobs[0].steps.filter(s=>s.name!=="Execute opt-in structured CFG as linked Linux Native AOT");
  assert.match(linuxVerify(linuxRun,j).join(" "),/missing successful step/);
});
test("strict Linux exact-source receipt stage cannot be omitted",()=>{
  const j=copy(linuxJobs);
  j.jobs[0].steps=j.jobs[0].steps.filter(s=>s.name!=="Record exact-head Native AOT execution receipt");
  assert.match(linuxVerify(linuxRun,j).join(" "),/missing successful step/);
});
test("strict Linux skipped optional source-local job cannot disguise missing hosted execution",()=>{
  const j=copy(linuxJobs);j.jobs.shift();j.total_count=1;
  assert.match(linuxVerify(linuxRun,j).join(" "),/missing or duplicated/);
});
test("strict Linux source-local success needs separate trusted source-tree receipt",()=>{
  const j=copy(linuxJobs);j.jobs[1].conclusion="success";
  j.jobs[1].steps=[{name:"Run native tests",number:1,status:"completed",conclusion:"success"}];
  assert.match(linuxVerify(linuxRun,j).join(" "),/separate independently verified tested-source receipt/);
});
test("strict Linux optional skip is bound to the run and attempt",()=>{
  const j=copy(linuxJobs);j.jobs[1].run_attempt=2;
  assert.match(linuxVerify(linuxRun,j).join(" "),/different attempt/);
});
test("strict Linux mandatory named stages exactly match the committed workflow",()=>{
  assert.ok(linuxHosted,"source Linux hosted job must exist");
  assert.equal(linuxSteps.length,10);
  for(const stage of linuxSteps){
    const j=copy(linuxJobs);j.jobs[0].steps=j.jobs[0].steps.filter(x=>x.name!==stage);
    assert.match(linuxVerify(linuxRun,j).join(" "),/missing successful step/,stage);
  }
});

test("trusted source-local receipt records workflow SHA and ref, not triggering SHA",()=> {
  const record=linuxSource.split("      - name: Record source tree and released executable SHA-256 identities\n")[1]
    ?.split("      - name: Upload bounded source-local Native AOT receipt")[0];
  assert.ok(record,"source-local receipt step must exist");
  assert.ok(record.includes("workflow_sha=%s\\nworkflow_ref=%s"));
  assert.ok(record.includes('"$GITHUB_REPOSITORY" "$GITHUB_WORKFLOW_SHA" "$GITHUB_WORKFLOW_REF"'));
  assert.ok(!record.includes('"$GITHUB_REPOSITORY" "$GITHUB_SHA" "$GITHUB_REF"'));
});

test("trusted manual Linux runner requires preapproved workflow-file SHA before allocation",()=> {
  const local=linuxSource.split("  linux-x64-approved-source-local:\n")[1];
  assert.ok(local,"source-local runner job exists");
  const admission=local.split("    # This dedicated runner group")[0];
  const precheckout=local.split("      - name: Authorize reviewed source before any checkout\n")[1]
    ?.split("      - name: Check out approved immutable source")[0];
  assert.ok(precheckout,"pre-checkout authorization step exists");
  assert.ok(admission.includes("vars.ORESLANG_NET_SELFHOSTED_LINUX_WORKFLOW_SHA != ''"));
  assert.ok(admission.includes("github.workflow_sha == vars.ORESLANG_NET_SELFHOSTED_LINUX_WORKFLOW_SHA"));
  assert.ok(!admission.includes("github.sha == vars.ORESLANG_NET_SELFHOSTED_LINUX_WORKFLOW_SHA"));
  assert.ok(precheckout.includes("APPROVED_WORKFLOW_SHA: ${{ vars.ORESLANG_NET_SELFHOSTED_LINUX_WORKFLOW_SHA }}"));
  assert.ok(precheckout.includes('[[ "$APPROVED_WORKFLOW_SHA" =~ ^[0-9a-f]{40}$ ]]'));
  assert.ok(precheckout.includes('test "$GITHUB_WORKFLOW_SHA" = "$APPROVED_WORKFLOW_SHA"'));
});
test("unapproved, changed or empty workflow SHA cannot satisfy trusted admission",()=> {
  const local=linuxSource.split("  linux-x64-approved-source-local:\n")[1];
  const admission=local.split("    # This dedicated runner group")[0];
  const required=["vars.ORESLANG_NET_SELFHOSTED_LINUX_WORKFLOW_SHA != ''",
    "github.workflow_sha == vars.ORESLANG_NET_SELFHOSTED_LINUX_WORKFLOW_SHA"];
  for (const atom of required) assert.ok(admission.includes(atom),atom);
  const valid="a".repeat(40),changed="b".repeat(40);
  const admitted=(run,approved)=>required.every(atom=>admission.includes(atom))
    && /^[a-f0-9]{40}$/.test(approved) && run===approved;
  assert.equal(admitted(valid,valid),true);
  assert.equal(admitted(valid,""),false);
  assert.equal(admitted(changed,valid),false);
  assert.equal(admitted(valid,changed),false);
  for (const atom of required)
    assert.equal(required.every(x=>admission.replace(atom,"").includes(x)),false,
      "deleting admission clause must fail the test: "+atom);
});
