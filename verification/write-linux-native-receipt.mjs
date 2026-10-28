import {readFileSync, writeFileSync, lstatSync, realpathSync} from 'node:fs';
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
export function createReceipt(root, env) {
  const git = (...args) => execFileSync('git', ['-C', root, ...args], {encoding:'utf8'}).trim();
  requireThat(sha(env.EXPECTED_HEAD), 'invalid expected source head');
  const executionHead = git('rev-parse', 'HEAD');
  const carrier = env.GITHUB_REPOSITORY !== sourceRepository;
  // The separate carrier wrapper pins the source SHA/tree from the source API.
  // Its snapshot subtree must be byte/mode identical, not merely similar files.
  if (carrier) {
    requireThat(env.GITHUB_REPOSITORY === 'honeypot-r-us-test/hnpt-e2e', 'unrecognized execution repository');
    requireThat(sha(env.EXPECTED_SOURCE_TREE), 'missing carrier source tree');
    requireThat(git('rev-parse', 'HEAD:snapshot') === env.EXPECTED_SOURCE_TREE, 'carrier source tree mismatch');
  } else requireThat(executionHead === env.EXPECTED_HEAD, 'source head mismatch');
  const tree = carrier ? env.EXPECTED_SOURCE_TREE : git('rev-parse', 'HEAD^{tree}');
  git('diff', '--quiet', '--exit-code', 'HEAD', '--', '.');
  requireThat(git('ls-files', '--others', '--exclude-standard') === '', 'untracked source files');
  requireThat(/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ID ?? ''), 'invalid run ID');
  requireThat(/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ATTEMPT ?? ''), 'invalid run attempt');
  requireThat(sha(env.GITHUB_WORKFLOW_SHA), 'invalid workflow SHA');
  const regular = relative => {
    const path = resolve(root, relative);
    requireThat(realpathSync(path) === path && lstatSync(path).isFile(), 'unsafe evidence path: '+relative);
    return readFileSync(path);
  };
  const arithmeticBytes = regular('artifacts/evidence/linux-native-arithmetic.json');
  const arithmetic = JSON.parse(arithmeticBytes);
  requireThat(arithmetic.schema === 'oreslang.net.linux_native_arithmetic_v1' &&
    arithmetic.division_rejection_exit === 1 && arithmetic.division_emitted_cil === false &&
    arithmetic.overflow_exit === 70 && arithmetic.native_application_count === 5 &&
    JSON.stringify(arithmetic.outputs) === '[14,42,40,45]', 'arithmetic proof missing or invalid');
  const hashes = Object.fromEntries(binaries.map(path => {
    const bytes = regular(path);
    execFileSync('bash', ['verification/assert-linux-native-release.sh', path], {cwd:root, stdio:'pipe'});
    return [path, createHash('sha256').update(bytes).digest('hex')];
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
