import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { gitBlobSha, verifyCorpus, loadAndVerifyCorpus, materialize, REQUIRED_PATHS } from '../tools/actor-counter-parity.mjs';

const manifest = new URL('../contracts/actor-counter-identical-source-v1/corpus.json', import.meta.url);
const load = async () => loadAndVerifyCorpus(manifest);
const mutate = (value, fn) => { const copy = structuredClone(value); fn(copy); return copy; };

test('manifest pins exactly seven byte-identical Atto sources and has no executed claim', async () => {
  const corpus = await load();
  assert.equal(verifyCorpus(corpus), true);
  assert.equal(REQUIRED_PATHS.length, 7);
  for (const name of REQUIRED_PATHS) assert.equal(gitBlobSha(corpus.sourceUnits[name]), corpus.sourceUnitBlobs[name]);
  assert.deepEqual(corpus.verifiedBackends, []);
  assert.equal(corpus.status, 'CANDIDATE_NOT_EXECUTED');
});

test('detects source mutation, forged hash, invented files and premature verification', async () => {
  const c = await load();
  const src = 'src/counter/state.atto';
  assert.throws(() => verifyCorpus(mutate(c, x => { x.sourceUnits[src] += '\n'; })), /blob mismatch/);
  assert.throws(() => verifyCorpus(mutate(c, x => { x.sourceUnitBlobs[src] = '0'.repeat(40); })), /blob mismatch/);
  assert.throws(() => verifyCorpus(mutate(c, x => { x.sourceUnits['../escape.atto'] = 'bad\n'; })), /inventory/);
  assert.throws(() => verifyCorpus(mutate(c, x => { x.verifiedBackends = ['java']; })), /prematurely certified/);
  assert.throws(() => verifyCorpus(mutate(c, x => { x.status = 'PASS'; })), /prematurely certified/);
});

test('rejects receive lifecycle spoofing even with a re-pinned blob', async () => {
  const c = await load();
  const src = 'src/counter/receive.atto';
  const forged = mutate(c, x => {
    x.sourceUnits[src] = x.sourceUnits[src].replace('external mtd', 'pub mtd');
    x.sourceUnitBlobs[src] = gitBlobSha(x.sourceUnits[src]);
  });
  assert.throws(() => verifyCorpus(forged), /lifecycle ABI/);
});

test('materialization rejects overwrites and reproduces all pinned bytes', async () => {
  const c = await load();
  const temp = await mkdtemp(path.join(os.tmpdir(), 'atto-counter-corpus-'));
  const dest = path.join(temp, 'candidate');
  try {
    assert.equal(await materialize(c, dest), 7);
    for (const name of REQUIRED_PATHS) {
      assert.equal(gitBlobSha(await readFile(path.join(dest, name), 'utf8')), c.sourceUnitBlobs[name]);
    }
    await assert.rejects(materialize(c, dest));
    await assert.rejects(materialize(c, '/'));
  } finally { await rm(temp, { recursive: true, force: true }); }
});
