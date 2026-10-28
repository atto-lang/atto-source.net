// Run: node verification/trusted-source-local-aot-workflow.test.mjs
// Dependency-free regression for trusted-runner workflow separation.
import { strict as assert } from 'node:assert';
import { readFileSync, readdirSync } from 'node:fs';

const hosted = readFileSync('.github/workflows/strict-linux-native-aot.yml', 'utf8');
const trusted = readFileSync('.github/workflows/trusted-source-local-linux-aot.yml', 'utf8');

assert.match(hosted, /^  pull_request:\s*$/m);
assert.doesNotMatch(hosted, /\bself-hosted\b/);
assert.doesNotMatch(hosted, /oreslang-net-aot/);
assert.doesNotMatch(hosted, /linux-x64-approved-source-local/);

const triggers = trusted.match(/^on:\n((?:  [a-z_]+:\n)+)/m);
assert.ok(triggers, 'trusted workflow trigger stanza missing');
assert.equal(triggers[1], '  workflow_dispatch:\n');
assert.doesNotMatch(trusted, /^  (?:push|pull_request|pull_request_target|workflow_run):/m);
assert.equal((trusted.match(/^  linux-x64-approved-source-local:/gm) ?? []).length, 1);
assert.match(trusted, /^    runs-on:\n      group: oreslang-net-aot-trusted\n      labels: \[self-hosted, Linux, X64, oreslang-net-aot\]$/m);

const mandatory = [
  "github.event_name == 'workflow_dispatch'",
  "github.ref == 'refs/heads/main'",
  'github.ref_protected == true',
  "github.repository == 'ores-truffle-oreslang/oreslang-source.net'",
  "github.repository_id == '1419704375'",
  "vars.ORESLANG_NET_SELFHOSTED_LINUX == 'enabled'",
  "vars.ORESLANG_NET_SELFHOSTED_LINUX_SHA != ''",
  "vars.ORESLANG_NET_SELFHOSTED_LINUX_TREE != ''",
  'test "$GITHUB_REF_PROTECTED" = true',
  'test "$GITHUB_REPOSITORY_ID" = 1419704375',
  'ref: ${{ vars.ORESLANG_NET_SELFHOSTED_LINUX_SHA }}',
  'persist-credentials: false',
  'test "$(git rev-parse HEAD)" = "$APPROVED_HEAD"',
  'test "$(git rev-parse \'HEAD^{tree}\')" = "$APPROVED_TREE"',
  'tested_source_head',
  'tested_root_tree',
  'workflow_sha',
  'SOURCE_LOCAL_LINUX_NATIVE_AOT_STRUCTURED_SSA_45_PLUS_14_42_40',
];
for (const s of mandatory) {
  assert.ok(trusted.includes(s), 'missing trust/runtime invariant: ' + s);
}
assert.doesNotMatch(trusted, /github\.event\.pull_request/);
assert.doesNotMatch(trusted, /\bsecrets\./);
assert.match(trusted, /^permissions:\n  contents: read$/m);
assert.match(trusted, /^    permissions:\n      contents: read$/m);

const stages = [
  'Authorize reviewed source before any checkout',
  'Check out approved immutable source',
  'Confirm exact Git commit, tree, and Native AOT prerequisites',
  'Run source-free release gates and managed regressions',
];
const indices = stages.map(p => trusted.indexOf(p));
assert.ok(indices.every(x => x >= 0), 'mandatory verification step absent');
assert.ok(indices.every((x, i) => i === 0 || x > indices[i - 1]), 'trusted source verification order broken');
const hostedWindows = readFileSync('.github/workflows/windows-native-aot.yml', 'utf8');
const trustedWindows = readFileSync('.github/workflows/trusted-source-local-windows-aot.yml', 'utf8');
assert.match(hostedWindows, /^  pull_request:\s*$/m);
assert.doesNotMatch(hostedWindows, /self-hosted-windows-static-facts/);
assert.doesNotMatch(hostedWindows, /^    runs-on: \[self-hosted/m);
const wt = trustedWindows.match(/^on:\n((?:  [a-z_]+:\n)+)/m);
assert.ok(wt);
assert.equal(wt[1], '  workflow_dispatch:\n');
assert.doesNotMatch(trustedWindows, /^  (?:push|pull_request|pull_request_target|workflow_run):/m);
assert.match(trustedWindows, /^    runs-on:\n      group: oreslang-net-aot-trusted\n      labels: \[self-hosted, Windows, X64, oreslang-net-aot\]$/m);
for (const s of [
  "github.event_name == 'workflow_dispatch'",
  "github.ref == 'refs/heads/main'",
  "github.repository_id == '1419704375'",
  'github.ref_protected == true',
  "vars.ORESLANG_NET_SELFHOSTED_WINDOWS == 'enabled'",
  "vars.ORESLANG_NET_SELFHOSTED_WINDOWS_SHA != ''",
  "vars.ORESLANG_NET_SELFHOSTED_WINDOWS_TREE != ''",
  'ref: ${{ vars.ORESLANG_NET_SELFHOSTED_WINDOWS_SHA }}',
  'persist-credentials: false',
  "(& git rev-parse 'HEAD^{tree}').Trim()",
  'tested_source_head',
  'tested_root_tree',
  'workflow_sha',
]) assert.ok(trustedWindows.includes(s), 'Windows trusted invariant missing: ' + s);
assert.doesNotMatch(trustedWindows, /github\.event\.pull_request/);
assert.match(trustedWindows, /^permissions:\n  contents: read$/m);
const wp = ['Preflight authorized immutable source without checkout', 'Checkout exact approved commit',
 'Validate source head/tree before any compiler execution', 'Run .NET compiler and runtime regression harness']
 .map(s => trustedWindows.indexOf(s));
assert.ok(wp.every(x => x >= 0));
assert.ok(wp.every((x,i) => i === 0 || x > wp[i-1]), 'Windows trust stages out of order');

const fenceHosted = readFileSync('.github/workflows/net-fence-smoke.yml', 'utf8');
const fenceTrusted = readFileSync('.github/workflows/trusted-source-local-fence-windows.yml', 'utf8');
assert.match(fenceHosted, /^  pull_request:\s*$/m);
assert.doesNotMatch(fenceHosted, /source-local-windows-fence:/);
assert.doesNotMatch(fenceHosted, /^    runs-on: \[self-hosted/m);
assert.equal((fenceTrusted.match(/^on:\n((?:  [a-z_]+:\n)+)/m) ?? [])[1], '  workflow_dispatch:\n');
assert.match(fenceTrusted, /^    runs-on:\n      group: oreslang-net-aot-trusted\n      labels: \[self-hosted, Windows, X64, oreslang-net-aot\]$/m);
for (const s of [
 "github.ref == 'refs/heads/main'", 'github.ref_protected == true',
 "github.repository_id == '1419704375'",
 "vars.ORESLANG_NET_SELFHOSTED_WINDOWS_SHA != ''",
 "vars.ORESLANG_NET_SELFHOSTED_WINDOWS_TREE != ''",
 'ref: ${{ vars.ORESLANG_NET_SELFHOSTED_WINDOWS_SHA }}',
 'persist-credentials: false',
 "(& git rev-parse 'HEAD^{tree}').Trim()",
 'tested_source_head', 'tested_root_tree', 'workflow_sha',
 'FENCE_REPEAT_COUNT=16',
]) assert.ok(fenceTrusted.includes(s), 'Windows fence trust invariant missing: ' + s);
const stagesFence = ['Authorize protected-main Windows fence snapshot before checkout',
 'Checkout immutable approved Windows fence commit',
 'Verify SHA and root tree before Windows fence build',
 'Execute 16 independent bounded race-stress rounds'].map(s => fenceTrusted.indexOf(s));
assert.ok(stagesFence.every(x => x >= 0));
assert.ok(stagesFence.every((x,i) => i === 0 || x > stagesFence[i-1]));
// Every ordinary workflow must use a literal GitHub-hosted runner label.
// The previous scanner missed mixed inline lists (e.g. [ubuntu-latest,
// self-hosted]) and expression-based selectors that could resolve at runtime.
// Reject all unproven selectors, not only a few self-hosted spellings.
const hostedRunnerLabel = /^(?:ubuntu|windows|macos)-(?:latest|\d+(?:\.\d+)?(?:-arm)?)$/;
function hasUnapprovedRunnerSelector(yaml) {
  for (const line of yaml.split(/\r?\n/)) {
    if (/^\s*#/.test(line)) continue;
    // Flow-style job mappings and YAML merge keys can hide runner
    // assignments from a line-oriented plain-key inventory.
    if (/^\s{2}[A-Za-z_][\w-]*\s*:\s*\{/.test(line) ||
        /^\s*<<\s*:/.test(line) ||
        /\{[^#\n]*?(?:["']runs-on["']|runs-on)\s*:/.test(line)) return true;
    // YAML permits spaces before ':' and quoted mapping keys. Require the
    // canonical plain key, not just a hosted-looking value.
    const match = line.match(/^\s*(?:(["'])runs-on\1|runs-on)\s*:\s*(.*?)\s*$/);
    if (!match) continue;
    if (!/^\s*runs-on:\s+/.test(line)) return true;
    const value = match[2].replace(/\s+#.*$/, '').trim();
    if (!hostedRunnerLabel.test(value)) return true;
  }
  return false;
}
for (const selector of [
  'runs-on: [ubuntu-latest, self-hosted]',
  'runs-on : self-hosted',
  '"runs-on": self-hosted',
  "'runs-on': [self-hosted, Linux]",
  '  build: { runs-on: [self-hosted, Linux], steps: [] }',
  '  build: { "runs-on": [self-hosted, Linux], steps: [] }',
  '  <<: *untrusted_runner',
  'runs-on : ubuntu-latest',
  'runs-on: [self-hosted, Linux]',
  'runs-on: { group: attacker, labels: [self-hosted] }',
  'runs-on: ${{ matrix.runner }}',
  'runs-on: ${{ fromJSON(inputs.runner) }}',
  'runs-on: self-hosted',
  'runs-on:\n      group: attacker\n      labels: [self-hosted, Linux]',
]) {
  assert.equal(hasUnapprovedRunnerSelector(selector), true,
    'unsafe runner selector bypassed inventory: ' + selector);
}
for (const selector of [
  'runs-on: ubuntu-24.04',
  'runs-on: ubuntu-latest',
  'runs-on: windows-2022',
  'runs-on: macos-latest',
  'runs-on: ubuntu-latest # hosted runner',
]) {
  assert.equal(hasUnapprovedRunnerSelector(selector), false,
    'literal hosted runner incorrectly rejected: ' + selector);
}
// Runner-group administration must separately restrict access to these exact
// reviewed workflow paths at @refs/heads/main. Static tests cannot replace it.
const allowed = new Set(['trusted-source-local-linux-aot.yml',
  'trusted-source-local-windows-aot.yml', 'trusted-source-local-fence-windows.yml']);
for (const name of readdirSync('.github/workflows').filter(x => /\.ya?ml$/.test(x))) {
  const yaml = readFileSync('.github/workflows/' + name, 'utf8');
  if (!allowed.has(name)) {
    assert.ok(!hasUnapprovedRunnerSelector(yaml),
      'Nontrusted workflow has unproven runner selector: ' + name);
  } else {
    assert.equal((yaml.match(/^\s+runs-on:/gm) ?? []).length, 1,
      'Trusted workflow has extra runner allocation: ' + name);
    assert.equal((yaml.match(/^\s+group:\s*oreslang-net-aot-trusted\s*$/gm) ?? []).length, 1,
      'Trusted workflow runner group count changed: ' + name);
    assert.equal((yaml.match(/^on:\n((?:  [a-z_]+:\n)+)/m) ?? [])[1],
      '  workflow_dispatch:\n', 'Unsafe trusted workflow event: ' + name);
  }
}
console.log('TRUSTED_SOURCE_LOCAL_AOT_WORKFLOW_CONTRACT_PASSED');
