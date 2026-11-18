import {test} from 'node:test';
import assert from 'node:assert/strict';
import {inspectArtifactMetadata} from './artifact-provenance-gate.mjs';

const sha = 'ab'.repeat(20);
const tree = 'cd'.repeat(20);
const digest = 'sha256:' + 'ef'.repeat(32);
const repo = 'ores-truffle-oreslang/oreslang-source.net';
const repoId = 1419704375;
const expected = {
  repo, repositoryId: repoId, head: sha, tree, runId: 4123, attempt: 2,
  requiredNames: ['compiled-guest', 'test-receipt'],
};
const run = { id: 4123, run_attempt: 2, head_sha: sha, status: 'completed',
  conclusion: 'success', repository: {full_name: repo, id: repoId, private: true} };
const makeArtifact = (id, name) => ({
  id, name, size_in_bytes: 143, expired: false, digest,
  created_at: '2026-11-17T12:00:00Z', expires_at: '2026-11-24T12:00:00Z',
  workflow_run: {id: 4123, repository_id: repoId, head_repository_id: repoId, head_sha: sha},
});
const response = {total_count: 2, artifacts: [makeArtifact(123, 'compiled-guest'), makeArtifact(456, 'test-receipt')]};
const clone = x => structuredClone(x);
const inspect = (r=run, artifacts=response, e=expected) => inspectArtifactMetadata(r, artifacts, e);

test('valid artifact metadata is NEVER promoted to certified execution', () => {
  const result = inspect();
  assert.equal(result.classification, 'METADATA_CONSISTENT_UNCERTIFIED');
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.artifactIds, [123, 456]);
  assert.deepEqual(result.checks, {
    runAttemptBound: false, sourceTreeBound: false, downloadDigestChecked: false,
  });
});
const attacks = [
  ['missing trusted policy', (_, __, e) => null],
  ['foreign repo', (_, __, e) => {e.repo = 'foreign/atto-ci';}],
  ['wrong numeric repo identity', (_, __, e) => {e.repositoryId = 1;}],
  ['short commit SHA', (_, __, e) => {e.head = '12345';}],
  ['wrong source tree', (_, __, e) => {e.tree = 'bad';}],
  ['missing source tree', (_, __, e) => {delete e.tree;}],
  ['wrong run ID', (r) => {r.id = 1;}],
  ['wrong run attempt', (r) => {r.run_attempt = 1;}],
  ['missing run attempt', (r) => {delete r.run_attempt;}],
  ['wrong run head', (r) => {r.head_sha = 'ab'.repeat(19)+'cd';}],
  ['wrong run repository name', (r) => {r.repository.full_name = 'other/repo';}],
  ['wrong run numeric repository ID', (r) => {r.repository.id = 5;}],
  ['run not private', (r) => {r.repository.private = false;}],
  ['failed workflow', (r) => {r.conclusion = 'failure';}],
  ['queued workflow', (r) => {r.status = 'queued';}],
  ['paginated list', (_, a) => {a.total_count = 3;}],
  ['empty artifact list', (_, a) => {a.total_count = 0; a.artifacts = [];}],
  ['missing required artifact', (_, a) => {a.artifacts[1].name = 'other-artifact';}],
  ['duplicate artifact id', (_, a) => {a.artifacts[1].id = 123;}],
  ['duplicate artifact name', (_, a) => {a.artifacts[1].name = 'compiled-guest';}],
  ['artifact with empty file', (_, a) => {a.artifacts[0].size_in_bytes = 0;}],
  ['boolean artifact size', (_, a) => {a.artifacts[0].size_in_bytes = true;}],
  ['expired artifact', (_, a) => {a.artifacts[0].expired = true;}],
  ['missing expired metadata', (_, a) => {delete a.artifacts[0].expired;}],
  ['missing SHA256 digest', (_, a) => {delete a.artifacts[0].digest;}],
  ['malformed SHA256 digest', (_, a) => {a.artifacts[0].digest = 'sha256:hello';}],
  ['missing timestamp', (_, a) => {delete a.artifacts[0].created_at;}],
  ['invalid expiry timestamp', (_, a) => {a.artifacts[0].expires_at = '2025-01-01T12:00:00Z';}],
  ['forged artifact run id', (_, a) => {a.artifacts[0].workflow_run.id = 4;}],
  ['forged artifact repository id', (_, a) => {a.artifacts[0].workflow_run.repository_id = 4;}],
  ['forged artifact head repo id', (_, a) => {a.artifacts[0].workflow_run.head_repository_id = 4;}],
  ['forged artifact head SHA', (_, a) => {a.artifacts[0].workflow_run.head_sha = '01'.repeat(20);}],
  ['missing workflow origin', (_, a) => {delete a.artifacts[0].workflow_run;}],
  ['invisible Unicode artifact label', (_, a) => {a.artifacts[0].name += '\u200b';}],
  ['combining mark artifact label', (_, a) => {a.artifacts[0].name += '\u034f';}],
  ['duplicate required policy names', (_, __, e) => {e.requiredNames.push('compiled-guest');}],
  ['null expected names', (_, __, e) => {e.requiredNames = null;}],
  ['boolean run ID', (_, __, e) => {e.runId = true;}],
  ['object SHA must not pass', (_, __, e) => {e.head = {toString: 'bad'};}],
  ['object digest must not throw', (_, a) => {a.artifacts[0].digest = {toString: 'bad'};}],
  ['object artifact name must not throw', (_, a) => {a.artifacts[0].name = {toString: 'bad'};}],
  ['object required name must not throw', (_, __, e) => {e.requiredNames[0] = {toString: 'bad'};}],
  ['boolean attempt', (_, __, e) => {e.attempt = true;}],
];
for (const [name, mutate] of attacks) {
  test('deny: ' + name, () => {
    const r=clone(run), a=clone(response), e=clone(expected);
    const maybe=mutate(r,a,e);
    const result=inspect(r,a,name==='missing trusted policy'?maybe:e);
    assert.equal(result.classification, 'REJECTED', `${name}: ${JSON.stringify(result)}`);
    assert.ok(result.errors.length > 0);
    assert.deepEqual(result.artifactIds, []);
    assert.equal(result.checks.downloadDigestChecked, false);
  });
}

test('does not mutate caller-owned records', () => {
  const r=clone(run), a=clone(response), e=clone(expected);
  inspect(r,a,e);
  assert.deepEqual(r,run); assert.deepEqual(a,response); assert.deepEqual(e,expected);
});
