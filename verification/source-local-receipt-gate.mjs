// Read-only preflight of independently acquired GitHub run/job metadata and
// source-local receipts. This is NOT a Native AOT, artifact, or release proof.
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const repo = 'ores-truffle-oreslang/oreslang-source.net';
const hex40 = /^[0-9a-f]{40}$/;
const hex64 = /^[0-9a-f]{64}$/;
const own = (obj, field) => Object.hasOwn(obj ?? {}, field);
const jobsRequired = Object.freeze({
  linux: {
    name: 'linux-x64-approved-source-local',
    file: 'trusted-source-local-linux-aot.yml',
    steps: [
      'Authorize reviewed source before any checkout',
      'Check out approved immutable source, not a PR workflow',
      'Confirm exact Git commit, tree, and Native AOT prerequisites',
      'Run source-free release gates and managed regressions',
      'Publish native compiler and three isolated native applications',
      'Publish structured CFG as fourth independent Native AOT executable',
      'Record source tree and released executable SHA-256 identities',
      'Upload bounded source-local Native AOT receipt',
    ],
  },
  windows: {
    name: 'self-hosted-windows-static-facts',
    file: 'trusted-source-local-windows-aot.yml',
    steps: [
      'Preflight authorized immutable source without checkout',
      'Checkout exact approved commit',
      'Validate source head/tree before any compiler execution',
      'Check installed Windows Native AOT toolchain',
      'Run .NET compiler and runtime regression harness',
      'Compile verified C# and F# source on trusted exact-head runner',
      'Verify real scalar and experimental structured static facts',
      'Publish and verify released Native AOT compiler',
      'Execute real published Windows Native AOT application',
      'Match Native AOT static facts against managed facts at exact head',
      'Record immutable source and native Windows executable hashes',
      'Upload immutable Windows Native AOT evidence',
    ],
  },
  fence: {
    name: 'source-local-windows-fence',
    file: 'trusted-source-local-fence-windows.yml',
    steps: [
      'Authorize protected-main Windows fence snapshot before checkout',
      'Checkout immutable approved Windows fence commit',
      'Verify SHA and root tree before Windows fence build',
      'Install .NET 10',
      'Build isolated fence harness with warnings as errors',
      'Execute 16 independent bounded race-stress rounds',
      'Record verified source-bound fence test receipt',
      'Upload Windows fence evidence',
    ],
  },
});
const linuxPaths = [
  'artifacts/compiler/linux-x64/oresnet',
  'artifacts/apps/linux-x64/oreslang-app',
  'artifacts/parity/linux-x64/oreslang-app',
  'artifacts/parity-precedence/linux-x64/oreslang-app',
  'artifacts/structured/linux-x64/oreslang-app',
];
const windowsPaths = [
  'artifacts/compiler/win-x64/oresnet.exe',
  'artifacts/apps/win-x64/oreslang-app.exe',
];
const isInt = (value) => Number.isSafeInteger(value) && value > 0;

function parseLinux(text) {
  if (typeof text !== 'string') throw new Error('linux receipt must be text');
  const data = {};
  const hashes = {};
  for (const line of text.trim().split(/\r?\n/)) {
    const kv = line.match(/^([a-z_]+)=([^\s]+)$/);
    const hash = line.match(/^([a-f0-9]{64})  (artifacts\/[a-zA-Z0-9./_-]+)$/);
    if (kv) {
      if (own(data, kv[1])) throw new Error('duplicate linux receipt field: ' + kv[1]);
      data[kv[1]] = kv[2];
    } else if (hash) {
      if (own(hashes, hash[2])) throw new Error('duplicate linux binary: ' + hash[2]);
      hashes[hash[2]] = hash[1];
    } else throw new Error('unrecognized linux receipt line');
  }
  const expectedFields = ['repository', 'workflow_sha', 'workflow_ref', 'tested_source_head',
    'tested_root_tree', 'run_id', 'run_attempt', 'target_rid'];
  if (Object.keys(data).sort().join(',') !== expectedFields.sort().join(','))
    throw new Error('linux receipt metadata keys mismatch');
  if (Object.keys(hashes).sort().join(',') !== linuxPaths.slice().sort().join(','))
    throw new Error('linux binaries incomplete, extraneous, or mislabeled');
  for (const digest of Object.values(hashes))
    if (!hex64.test(digest)) throw new Error('invalid linux sha256 digest');
  if (data.target_rid !== 'linux-x64') throw new Error('unexpected linux RID');
  return { ...data, binaries: hashes };
}

function assertBinaries(binaries) {
  if (!Array.isArray(binaries) || binaries.length !== windowsPaths.length)
    throw new Error('windows binary receipt count mismatch');
  const seen = new Set();
  for (const binary of binaries) {
    if (!binary || !windowsPaths.includes(binary.path) || !hex64.test(binary.sha256))
      throw new Error('windows binary path or digest invalid');
    if (seen.has(binary.path)) throw new Error('duplicate windows binary path');
    seen.add(binary.path);
  }
}

export function verifySourceLocalBundle(bundle) {
  const errors = [];
  const fail = (ok, msg) => { if (!ok) errors.push(msg); };
  if (!bundle || typeof bundle !== 'object' || Array.isArray(bundle))
    return ['missing bundle object'];
  const { approval, receipts, execution } = bundle;
  if (!approval || !receipts || !execution) return ['missing approval, receipts, or execution'];
  fail(approval.repository === repo, 'source repository mismatch');
  fail(hex40.test(approval.source_head ?? ''), 'invalid approved source SHA');
  fail(hex40.test(approval.root_tree ?? ''), 'invalid approved root tree');
  fail(approval.independent_review_verified === true,
    'independent review has not been verified');
  fail(approval.protected_main_verified === true,
    'protected main has not been independently verified');
  fail(approval.restricted_runner_group_verified === true,
    'runner group restrictions have not been independently verified');
  const usedRuns = new Set();
  for (const [kind, spec] of Object.entries(jobsRequired)) {
    const expected = approval.workflows?.[kind];
    const run = execution[kind]?.run;
    const jobs = execution[kind]?.jobs;
    if (!expected || !run || !jobs) { errors.push(kind + ': missing independently fetched run/jobs or approval'); continue; }
    const expectedRef = repo + '/.github/workflows/' + spec.file + '@refs/heads/main';
    fail(hex40.test(expected.workflow_sha ?? ''), kind + ': invalid approved workflow SHA');
    // The SHA recorded for a workflow definition can differ from the default
    // branch tip that received workflow_dispatch. Authorize BOTH explicitly.
    fail(hex40.test(expected.dispatch_head_sha ?? ''),
      kind + ': invalid approved dispatch head SHA');
    fail(isInt(expected.run_id) && isInt(expected.attempt), kind + ': bad run identity');
    fail(!usedRuns.has(expected.run_id), kind + ': duplicate run ID across workflows');
    usedRuns.add(expected.run_id);
    fail(run.id === expected.run_id && run.run_attempt === expected.attempt,
      kind + ': actual run ID/attempt mismatch');
    fail(run.repository?.full_name === repo && run.event === 'workflow_dispatch',
      kind + ': run not from expected source-local manual workflow');
    fail(run.head_branch === 'main' && run.head_sha === expected.dispatch_head_sha,
      kind + ': run did not execute independently approved main dispatch head');
    fail(run.status === 'completed' && run.conclusion === 'success',
      kind + ': run not successful');
    fail(run.path === '.github/workflows/' + spec.file,
      kind + ': unexpected workflow path');
    fail(jobs.total_count === 1 && Array.isArray(jobs.jobs) && jobs.jobs.length === 1,
      kind + ': jobs response incomplete or contains unexpected jobs');
    const job = jobs.jobs?.[0];
    if (!job) { errors.push(kind + ': missing job'); continue; }
    fail(job.name === spec.name && job.run_id === expected.run_id &&
      job.run_attempt === expected.attempt, kind + ': job identity mismatch');
    fail(job.status === 'completed' && job.conclusion === 'success' && isInt(job.runner_id),
      kind + ': job not successful on a real allocated runner');
    const steps = job.steps;
    fail(Array.isArray(steps) && steps.length === spec.steps.length,
      kind + ': missing or extra executed steps');
    if (Array.isArray(steps)) {
      for (let i = 0; i < spec.steps.length; i++) {
        const step = steps[i];
        fail(step?.name === spec.steps[i] && step?.number === i + 1 &&
          step?.status === 'completed' && step?.conclusion === 'success',
          kind + ': missing or unsuccessful step ' + spec.steps[i]);
      }
    }
    let receipt = receipts[kind];
    if (kind === 'linux') {
      try { receipt = parseLinux(receipt); }
      catch (err) { errors.push('linux: ' + err.message); continue; }
    } else if (!receipt || typeof receipt !== 'object' || Array.isArray(receipt)) {
      errors.push(kind + ': invalid JSON receipt'); continue;
    }
    fail(receipt.repository === repo, kind + ': receipt repository mismatch');
    fail(receipt.workflow_sha === expected.workflow_sha &&
      receipt.workflow_ref === expectedRef, kind + ': receipt workflow identity mismatch');
    fail(receipt.tested_source_head === approval.source_head &&
      receipt.tested_root_tree === approval.root_tree,
      kind + ': receipt source SHA/root tree mismatch');
    fail(String(receipt.run_id) === String(expected.run_id) &&
      String(receipt.run_attempt) === String(expected.attempt),
      kind + ': receipt run identity mismatch');
    if (kind === 'windows') {
      fail(receipt.schema === 'oreslang.net.source_local_windows_aot_v1',
        'windows: invalid receipt schema');
      try { assertBinaries(receipt.binaries); }
      catch (err) { errors.push('windows: ' + err.message); }
    } else if (kind === 'fence') {
      fail(receipt.schema === 'oreslang.net.windows_fence_source_local_v1',
        'fence: invalid receipt schema');
      fail(receipt.rounds === 16 && receipt.test_marker === 'ISOLATED_FENCE_TESTS_PASSED',
        'fence: 16 successful stress rounds not proven by receipt');
    }
  }
  return errors;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (process.argv.length !== 3) throw new Error('usage: node source-local-receipt-gate.mjs independently-acquired-bundle.json');
    const bundle = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    const errors = verifySourceLocalBundle(bundle);
    if (errors.length) { for (const error of errors) console.error('DENIED: ' + error); process.exitCode = 1; }
    else console.log('SOURCE_LOCAL_RECEIPT_METADATA_CONSISTENT (NOT RELEASE_CERTIFIED; independently verify artifacts, binary hashes, runner configuration, reviewer permissions and logs)');
  } catch (error) { console.error('DENIED: ' + error.message); process.exitCode = 1; }
}
