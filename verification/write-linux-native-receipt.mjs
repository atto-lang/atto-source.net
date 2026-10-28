import {readFileSync, writeFileSync, lstatSync, realpathSync, statSync, openSync, readSync, closeSync, fstatSync, constants} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {resolve, dirname} from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';

export const sourceRepository = 'ores-truffle-oreslang/oreslang-source.net';
export const binaries = [
  'artifacts/compiler/linux-x64/oresnet',
  'artifacts/apps/linux-x64/oreslang-app',
  'artifacts/parity/linux-x64/oreslang-app',
  'artifacts/parity-precedence/linux-x64/oreslang-app',
  'artifacts/structured/linux-x64/oreslang-app',
  'artifacts/negative-overflow/linux-x64/oreslang-app'
];
const sha = value => typeof value === 'string' && /^[a-f0-9]{40}$/.test(value);
const requireThat = (value, message) => { if (!value) throw Error(message); };
// The native binaries may be large. Hash in bounded chunks rather than
// materializing each Native AOT image on the Node heap.
export function sha256RegularFile(root, relative) {
  const path = resolve(root, relative);
  requireThat(realpathSync(path) === path && lstatSync(path).isFile(),
    'unsafe evidence path: '+relative);
  const hash = createHash('sha256');
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    requireThat(fstatSync(fd).isFile(), 'not a regular evidence file: '+relative);
    let length;
    while ((length = readSync(fd, buffer, 0, buffer.length, null)) > 0)
      hash.update(buffer.subarray(0, length));
  } finally {
    closeSync(fd);
  }
  return hash.digest('hex');
}
export function createReceipt(root, env) {
  const git = (...args) => execFileSync('git', ['-C', root, ...args], {encoding:'utf8'}).trim();
  requireThat(sha(env.EXPECTED_HEAD), 'invalid expected source head');
  const executionHead = git('rev-parse', 'HEAD');
  // This receipt is for source-local execution, not an authorization to move
  // private code to a funded carrier. Even a matching snapshot subtree cannot
  // establish the separately required independent transfer approval.
  // Only the originating repository may issue an execution receipt here.
  requireThat(env.GITHUB_REPOSITORY === sourceRepository,
    'source-local receipt requires originating repository; external carrier not approved');
  requireThat(executionHead === env.EXPECTED_HEAD, 'source head mismatch');
  const tree = git('rev-parse', 'HEAD^{tree}');
  git('diff', '--quiet', '--exit-code', 'HEAD', '--', '.');
  requireThat(git('ls-files', '--others', '--exclude-standard') === '', 'untracked source files');
  requireThat(/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ID ?? ''), 'invalid run ID');
  requireThat(/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ATTEMPT ?? ''), 'invalid run attempt');
  requireThat(sha(env.GITHUB_WORKFLOW_SHA), 'invalid workflow SHA');
  const regular = relative => {
    const path = resolve(root, relative);
    requireThat(realpathSync(path) === path && lstatSync(path).isFile(), 'unsafe evidence path: '+relative);
    return path;
  };
  const arithmeticPath = regular('artifacts/evidence/linux-native-arithmetic.json');
  requireThat(statSync(arithmeticPath).size <= 65536,
    'oversized arithmetic evidence');
  const arithmeticBytes = readFileSync(arithmeticPath);
  const arithmetic = JSON.parse(arithmeticBytes);
  requireThat(arithmetic.schema === 'oreslang.net.linux_native_arithmetic_v1' &&
    arithmetic.division_rejection_exit === 1 && arithmetic.division_emitted_cil === false &&
    arithmetic.overflow_exit === 70 && arithmetic.native_application_count === 5 &&
    JSON.stringify(arithmetic.outputs) === '[14,42,40,45]', 'arithmetic proof missing or invalid');
  const hashes = Object.fromEntries(binaries.map(path => {
    execFileSync('bash', ['verification/assert-linux-native-release.sh', path], {cwd:root, stdio:'pipe'});
    return [path, sha256RegularFile(root, path)];
  }));
  return {
    schema:'oreslang.net.exact_head_native_aot_v2', repository:sourceRepository,
    source_head_sha:env.EXPECTED_HEAD, tree_sha:tree,
    execution_repository:env.GITHUB_REPOSITORY, execution_head_sha:executionHead,
    workflow_sha:env.GITHUB_WORKFLOW_SHA, workflow_ref:env.GITHUB_WORKFLOW_REF,
    run_id:env.GITHUB_RUN_ID, run_attempt:env.GITHUB_RUN_ATTEMPT,
    runner_os:'Linux', target_rid:'linux-x64', arithmetic,
    arithmetic_sha256:createHash('sha256').update(arithmeticBytes).digest('hex'),
    binary_sha256:hashes, source_only:false, independently_verified:false,
    checks:['managed-tests','native-compiler','negative-source-no-cil',
      'division-rejection-no-cil','checked-overflow-exit70','source-isolation-5',
      'canonical-precedence','structured-ssa-cil-native45','sidecar-negative']
  };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  const receipt = createReceipt(root, process.env);
  writeFileSync(resolve(root, 'artifacts/evidence/linux-native-aot.json'), JSON.stringify(receipt,null,2)+'\n');
  console.log('LINUX_NATIVE_AOT_RECEIPT source='+receipt.source_head_sha+' tree='+receipt.tree_sha);
}
