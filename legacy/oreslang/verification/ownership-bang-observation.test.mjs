import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Snapshot-integrity regressions ONLY. These tests never establish that CI ran.
const ledger = JSON.parse(readFileSync(new URL('../docs/evidence/ownership-bang-20261116-source-head-ledger.json', import.meta.url), 'utf8'));
const sha = /^[a-f0-9]{40}$/;
const expected = [
  ['attolang-language-spec.md', 67, 1444921662],
  ['oreslang-format', 144, 1406328272],
  ['oreslang-source.java', 3112, 1400792274],
  ['oreslang-source.llvm', 1831, 1410897874],
  ['oreslang-source.net', 531, 1419704375],
  ['oreslang-source.wasm', 490, 1419858954],
  ['oreslang-source.rs', 71, 1426284360],
  ['oreslang-source.cranelift', 117, 1437273004],
  ['oreslang-source.js', 254, 1423606573],
  ['oreslang-source.hdl', 46, 1423785959],
];

function verify_observation(snapshot) {
  assert.equal(snapshot.schema, 'oreslang-ownership-bang-ci-observation-v1');
  assert.equal(snapshot.release_claim, 'BLOCKED_NOT_EXECUTED');
  assert.equal(snapshot.security.source_main_branch_protected, false);
  assert.equal(snapshot.security.independent_source_export_reviewer_enabled, false);
  assert.equal(snapshot.security.source_export_authorized, false);
  assert.equal(snapshot.security.active_source_export_destination_approvals, 0);
  assert.equal(snapshot.security.active_exact_snapshot_approvals, 0);
  assert.equal(snapshot.sources.length, expected.length);
  const seen = new Set();
  snapshot.sources.forEach((source, idx) => {
    const [name, pr, id] = expected[idx];
    assert.equal(source.repository, 'ores-truffle-oreslang/' + name);
    assert.equal(source.pull_request, pr);
    assert.equal(source.repository_id, id);
    assert.ok(sha.test(source.head_sha), 'full SHA required');
    assert.ok(sha.test(source.git_root_tree_sha), 'full root tree SHA required');
    assert.ok(!seen.has(source.repository), 'duplicate repository');
    seen.add(source.repository);
    const receipt = source.sample;
    assert.ok(Number.isSafeInteger(receipt.run_id) && receipt.run_id > 0);
    assert.ok(Number.isSafeInteger(receipt.job_id) && receipt.job_id > 0);
    assert.equal(receipt.run_status, 'completed');
    assert.equal(receipt.job_status, 'completed');
    assert.ok(['failure', 'cancelled'].includes(receipt.run_conclusion));
    assert.equal(receipt.job_conclusion, receipt.run_conclusion);
    assert.equal(receipt.observed_executed_step_count, 0);
    assert.equal(receipt.inspected_run_artifact_count, 0);
    assert.equal(receipt.classification, 'NOT_EXECUTED');
  });
  assert.equal(snapshot.source_free_probe_samples.length, 2);
  for (const probe of snapshot.source_free_probe_samples) {
    assert.ok(sha.test(probe.head_sha));
    assert.ok(Number.isSafeInteger(probe.run_id) && probe.run_id > 0);
    assert.ok(Number.isSafeInteger(probe.job_id) && probe.job_id > 0);
    assert.equal(probe.observed_executed_step_count, 0);
    assert.equal(probe.inspected_run_artifact_count, 0);
    assert.equal(probe.classification, 'NOT_EXECUTED');
  }
  // A consistent historical snapshot remains NON-CERTIFYING by construction.
  return 'HISTORICAL_SNAPSHOT_CONSISTENT_NOT_CI_EXECUTED';
}

test('snapshot is structurally sound but cannot certify CI', () => {
  assert.equal(verify_observation(ledger), 'HISTORICAL_SNAPSHOT_CONSISTENT_NOT_CI_EXECUTED');
});

const attacks = [
  ['claim forged success', s => { s.release_claim = 'PASSED'; }],
  ['export toggled', s => { s.security.source_export_authorized = true; }],
  ['reviewer toggled', s => { s.security.independent_source_export_reviewer_enabled = true; }],
  ['snapshot approval fabricated', s => { s.security.active_exact_snapshot_approvals = 1; }],
  ['destination approval fabricated', s => { s.security.active_source_export_destination_approvals = 1; }],
  ['protected main fabricated', s => { s.security.source_main_branch_protected = true; }],
  ['missing source', s => { s.sources.pop(); }],
  ['duplicate source', s => { s.sources[1] = structuredClone(s.sources[0]); }],
  ['forged repository identity', s => { s.sources[0].repository_id = 42; }],
  ['boolean identity substitution', s => { s.sources[0].repository_id = true; }],
  ['shortened SHA', s => { s.sources[0].head_sha = 'abc'; }],
  ['malformed tree', s => { s.sources[0].git_root_tree_sha = 'g'.repeat(40); }],
  ['run claim green', s => { s.sources[0].sample.run_conclusion = 'success'; }],
  ['job claim green', s => { s.sources[0].sample.job_conclusion = 'success'; }],
  ['steps invented', s => { s.sources[0].sample.observed_executed_step_count = 1; }],
  ['artifact invented', s => { s.sources[0].sample.inspected_run_artifact_count = 1; }],
  ['classification promoted', s => { s.sources[0].sample.classification = 'PASS'; }],
  ['canary promoted', s => { s.source_free_probe_samples[0].classification = 'PASS'; }],
];
for (const [name, mutate] of attacks) {
  test('fail closed: ' + name, () => {
    const sample = structuredClone(ledger);
    mutate(sample);
    assert.throws(() => verify_observation(sample));
  });
}
