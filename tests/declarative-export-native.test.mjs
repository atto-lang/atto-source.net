import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const fixtures = join(root, 'tests', 'fixtures', 'declarative-exports');
const available = spawnSync('dotnet', ['--version'], { encoding: 'utf8' }).status === 0;
function check(name) {
  return spawnSync('dotnet', [
    'run', '--project', join(root, 'compiler', 'Ores.Net.Compiler'),
    '--', 'check', join(fixtures, name)
  ], { cwd: root, encoding: 'utf8' });
}

test('native .NET scalar export pub succeeds at top and bottom', { skip: !available }, () => {
  for (const name of ['export-top.atto', 'export-bottom.atto']) {
    const result = check(name);
    assert.equal(result.status, 0, name + ' ' + result.stderr);
  }
});

test('package scope, nominal class and duplicate exports fail closed until real linker', { skip: !available }, () => {
  for (const [name, code] of [
    ['export-pkg-main-unsupported.atto', 'EXPORT_PACKAGE_LINKER_UNSUPPORTED'],
    ['export-class-unsupported.atto', 'EXPORT_BACKEND_UNSUPPORTED'],
    ['export-duplicate.atto', 'EXPORT_DUPLICATE'],
    ['export-missing-pub.atto', 'EXPORT_PUB_REQUIRED']
  ]) {
    const result = check(name);
    assert.notEqual(result.status, 0, name);
    assert.match(result.stderr + result.stdout, new RegExp(code));
  }
});
