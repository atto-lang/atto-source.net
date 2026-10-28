// Read-only GitHub Actions evidence validator. No network calls or tokens.
// The run and jobs JSON must be acquired independently from the originating
// GitHub repository API; fixtures alone never establish execution.
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const requiredByWorkflow = {
  "Strict .NET Linux x64 Native AOT": {
    "linux-x64-compiler-and-app": [
      "Verify exact source checkout",
      "Install Native AOT toolchain",
      "Check source scripts and managed compiler regressions",
      "Compile native compiler and first native application",
      "Prove second generated source gets its own Native AOT executable",
      "Execute shared native.precedence and prove three-source isolation",
      "Execute opt-in structured CFG as linked Linux Native AOT",
      "Execute Linux Native AOT checked-arithmetic negatives",
      "Negative test for managed sidecar rejection",
      "Record exact-head Native AOT execution receipt",
      "Upload Linux native executables and evidence"
    ]
  },
  "Windows Native AOT": {
    "windows-x64": [
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
      "Upload native executable"
    ],
    "linux-aot-diagnostic": [
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
      "Verify typed bindings in executed Linux Native AOT"
    ],
  },
  "Integration declaration integrity": {
    "declaration-integrity": [
      "Assert exact originating-source checkout",
      "Test Rust repository policy tooling",
      "Verify declaration-only manifest (not a runtime parity claim)"
    ]
  }
};

const requiredPathByWorkflow = {
  "Strict .NET Linux x64 Native AOT": ".github/workflows/strict-linux-native-aot.yml",
  "Windows Native AOT": ".github/workflows/windows-native-aot.yml",
  "Integration declaration integrity": ".github/workflows/integration-declarations.yml"
};
// Stable IDs verified against the source repository's actual Actions runs.
// A same-name or same-path spoof cannot replace the originating workflow.
const requiredWorkflowIdByWorkflow = {
  "Strict .NET Linux x64 Native AOT": 398765873,
  "Windows Native AOT": 384715065,
  "Integration declaration integrity": 388530125
};
export function verifyEvidence(run, response, expected) {
  const errors = [];
  const fail = (condition, explanation) => { if (!condition) errors.push(explanation); };
  const hex = /^[a-f0-9]{40}$/i;
  fail(hex.test(expected.head), "expected head must be a full 40-digit SHA");
  fail(/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(expected.repo), "invalid expected repository");
  // Matching caller-supplied repo strings cannot certify unauthorized private
  // source copying into a carrier: this ledger only validates source-org runs.
  fail(expected.repo === "ores-truffle-oreslang/oreslang-source.net",
    "native parity evidence requires the originating source repository");
  fail(Number.isSafeInteger(expected.runId) && expected.runId > 0, "expected run ID required");
  fail(Number.isSafeInteger(expected.attempt) && expected.attempt > 0, "expected attempt required");
  fail(Object.hasOwn(requiredByWorkflow,expected.workflow),"unrecognized workflow");
  if (run === null || typeof run !== "object" || Array.isArray(run)) {
    errors.push("run JSON must be an object");
    return errors;
  }
  fail(run.id === expected.runId, "run ID does not match independently selected run");
  fail(run.run_attempt === expected.attempt, "run attempt does not match");
  fail(run.repository?.full_name === expected.repo, "wrong run repository");
  fail(run.head_sha === expected.head, "run head is not the reviewed commit");
  fail(run.name === expected.workflow, "wrong workflow");
  fail(run.path === requiredPathByWorkflow[expected.workflow], "wrong workflow path");
  fail(run.workflow_id === requiredWorkflowIdByWorkflow[expected.workflow], "wrong workflow ID");
  fail(run.status === "completed" && run.conclusion === "success", "workflow has not passed");
  fail(["pull_request","workflow_dispatch","push"].includes(run.event), "unsupported workflow event");
  if (response === null || typeof response !== "object" || Array.isArray(response)) {
    errors.push("jobs JSON must be an object");
    return errors;
  }
  const jobs = response.jobs;
  if (!Array.isArray(jobs)) {
    errors.push("jobs array absent");
    return errors;
  }
  fail(Number.isInteger(response.total_count) && response.total_count === jobs.length && jobs.length > 0,
    "jobs list incomplete, empty or paginated");
  const requirements = requiredByWorkflow[expected.workflow] || {};
  const jobNames = new Set();
  for (const job of jobs) {
    if (!job || typeof job !== "object" || Array.isArray(job) ||
        typeof job.name !== "string" || job.name.trim() === "") {
      errors.push("jobs list has malformed job");
      continue;
    }
    fail(!jobNames.has(job.name), `duplicate job name: ${job.name}`);
    jobNames.add(job.name);
    fail(job.run_id === expected.runId, `job ${job.name} belongs to different run`);
    fail(job.run_attempt === expected.attempt, `job ${job.name} belongs to different attempt`);
    fail(job.head_sha === expected.head, `job ${job.name} has different source head`);
  }
  for (const [name, stepsRequired] of Object.entries(requirements)) {
    const matches=jobs.filter(job=>job && job.name===name);
    fail(matches.length===1, `required job ${name} missing or duplicated`);
    if (matches.length!==1) continue;
    const job=matches[0];
    if (expected.workflow === "Strict .NET Linux x64 Native AOT")
      fail(Number.isSafeInteger(job.runner_id) && job.runner_id > 0,
        "strict Linux runner was not allocated");
    fail(job.status==="completed" && job.conclusion==="success", `job ${name} not successful`);
    const steps=job.steps;
    if (!Array.isArray(steps) || steps.length===0) {
      errors.push(`job ${name} has zero executed steps`);
      continue;
    }
    const numbers=new Set();
    const names=new Set();
    for (const step of steps) {
      if (!step || typeof step !== "object" || Array.isArray(step) ||
          !Number.isSafeInteger(step.number) || step.number <= 0 ||
          typeof step.name !== "string" || step.name.trim() === "") {
        errors.push(`job ${name} has malformed step`);
        continue;
      }
      fail(!numbers.has(step.number), `job ${name} has duplicate step number`);
      numbers.add(step.number);
      fail(!names.has(step.name), `job ${name} has duplicate step name`);
      names.add(step.name);
      fail(step.status==="completed" && step.conclusion==="success",
        `job ${name} step ${step.name} was not successful`);
    }
    for (const stepName of stepsRequired) {
      fail(steps.some(s=>s && s.name===stepName && s.status==="completed" && s.conclusion==="success"),
        `job ${name} missing successful step: ${stepName}`);
    }
  }
  for (const job of jobs) {
    if (job && !Object.hasOwn(requirements,job.name))
      errors.push(`unexpected job ${job.name}: no unreviewed self-hosted or extra runner can qualify source-native execution`);
  }
  return errors;
}

function commandLine(args) {
  const options={};
  for (let i=0;i<args.length;i+=2) {
    if (!args[i]?.startsWith("--") || i+1>=args.length || args[i+1].startsWith("--"))
      throw new Error("expected --key value pairs");
    const key=args[i].slice(2);
    if (Object.hasOwn(options,key)) throw new Error("duplicate argument: "+key);
    options[key]=args[i+1];
  }
  for (const key of ["repo","head","run-id","attempt","workflow","run","jobs"])
    if (!options[key]) throw new Error("missing --"+key);
  if (!/^[0-9]+$/.test(options["run-id"]) || !/^[0-9]+$/.test(options.attempt))
    throw new Error("positive decimal run ID and attempt required");
  return {options,expected:{repo:options.repo,head:options.head,
    runId:Number(options["run-id"]),attempt:Number(options.attempt),
    workflow:options.workflow}};
}
if (process.argv[1] && import.meta.url===pathToFileURL(process.argv[1]).href) {
  try {
    const {options,expected}=commandLine(process.argv.slice(2));
    const run=JSON.parse(readFileSync(options.run,"utf8"));
    const jobs=JSON.parse(readFileSync(options.jobs,"utf8"));
    const errors=verifyEvidence(run,jobs,expected);
    if (errors.length!==0) {
      for (const error of errors) console.error("DENIED: "+error);
      process.exitCode=1;
    } else {
      console.log("EVIDENCE_ACCEPTED repo="+expected.repo+
        " head="+expected.head+" run="+expected.runId+
        " attempt="+expected.attempt+" workflow="+expected.workflow);
    }
  } catch (err) {
    console.error("DENIED: "+String(err?.message??err));
    process.exitCode=1;
  }
}
