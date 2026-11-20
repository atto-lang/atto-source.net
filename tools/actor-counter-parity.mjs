import { createHash } from 'node:crypto';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

export const REQUIRED_PATHS = Object.freeze([
  '.atto.pkg.toml',
  'src/counter/actor.atto',
  'src/counter/contract.atto',
  'src/counter/main.atto',
  'src/counter/method.atto',
  'src/counter/receive.atto',
  'src/counter/state.atto',
]);

export function gitBlobSha(content) {
  const bytes = Buffer.from(content, 'utf8');
  return createHash('sha1').update('blob ' + bytes.length + '\0').update(bytes).digest('hex');
}

// Static manifest integrity ONLY. This function never runs a guest program.
export function verifyCorpus(corpus) {
  if (corpus?.schemaVersion !== 1 || corpus.id !== 'actor-counter-identical-source-v1' ||
      corpus.status !== 'CANDIDATE_NOT_EXECUTED' ||
      JSON.stringify(corpus.targets) !== JSON.stringify(['java', 'llvm', 'net']) ||
      JSON.stringify(corpus.verifiedBackends) !== '[]' ||
      corpus.expected?.stdout !== '1|3|6|10' || corpus.expected?.exitCode !== 0 ||
      JSON.stringify(corpus.expected.messages) !== '[1,2,3,4]') {
    throw new Error('Invalid or prematurely certified Counter contract metadata');
  }
  for (const key of ['sourceUnits', 'sourceUnitBlobs']) {
    const value = corpus[key];
    if (!value || JSON.stringify(Object.keys(value).sort()) !== JSON.stringify(REQUIRED_PATHS)) {
      throw new Error('Invalid Counter ' + key + ' inventory');
    }
  }
  for (const name of REQUIRED_PATHS) {
    const contents = corpus.sourceUnits[name];
    if (typeof contents !== 'string' || contents.length === 0 ||
        !contents.endsWith('\n') || contents.includes('\r') || contents.includes('\0') ||
        gitBlobSha(contents) !== corpus.sourceUnitBlobs[name]) {
      throw new Error('Counter source blob mismatch: ' + name);
    }
  }
  const receiver = corpus.sourceUnits['src/counter/receive.atto'];
  if (!/^external mtd \(Counter c\) receive\(ActorMail<int> mail\): void \{/m.test(receiver) ||
      /^\s*(?:pub\s+)?mtd \(Counter c\) receive\(/m.test(receiver) ||
      !receiver.includes('self.handle_state(critical-write |s|') ||
      !receiver.includes('self.handle_state(critical-read |s|')) {
    throw new Error('Counter lifecycle ABI or compiler-owned critical scopes drifted');
  }
  // A Git blob alone is not an ownership check. Lock this canonical candidate
  // to a detached scalar whose critical-read loan ends before mailbox send.
  const detached = '  val int snapshot = self.handle_state(critical-read |s| -> s.total);\n' +
      '  self.send(snapshot);\n';
  if (!receiver.includes(detached) ||
      (receiver.match(/self\.send\s*\(/g) ?? []).length !== 1 ||
      /self\.send\s*\(\s*s\./.test(receiver)) {
    throw new Error('Counter must release critical-read before mailbox send');
  }
  return true;
}

export async function loadAndVerifyCorpus(manifestPath) {
  const corpus = JSON.parse(await readFile(manifestPath, 'utf8'));
  verifyCorpus(corpus);
  return corpus;
}

// Refuse an existing destination, including symlinks; no silent overwrite.
export async function materialize(corpus, destination) {
  verifyCorpus(corpus);
  if (typeof destination !== 'string' || !destination || !path.isAbsolute(destination) ||
      path.parse(destination).root === destination) {
    throw new Error('Materialization requires a new absolute non-root destination directory');
  }
  await mkdir(destination);
  for (const name of REQUIRED_PATHS) {
    const target = path.join(destination, ...name.split('/'));
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, corpus.sourceUnits[name], { flag: 'wx', mode: 0o600 });
  }
  return REQUIRED_PATHS.length;
}

const here = path.dirname(fileURLToPath(import.meta.url));
const canonical = path.resolve(here, '../contracts/actor-counter-identical-source-v1/corpus.json');
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const corpus = await loadAndVerifyCorpus(canonical);
    const command = process.argv[2] ?? 'verify';
    if (command === 'verify' && process.argv.length === 3) {
      console.log('CONTRACT_VALID actor-counter-identical-source-v1; GUEST_NOT_EXECUTED');
    } else if (command === 'materialize' && process.argv.length === 4) {
      const count = await materialize(corpus, process.argv[3]);
      console.log('MATERIALIZED ' + count + ' pinned sources; GUEST_NOT_EXECUTED');
    } else {
      throw new Error('Usage: node tools/actor-counter-parity.mjs verify | materialize /absolute/new/dir');
    }
  } catch (e) {
    console.error('COUNTER_PARITY_CONTRACT_FAILED: ' + e.message);
    process.exitCode = 1;
  }
}
