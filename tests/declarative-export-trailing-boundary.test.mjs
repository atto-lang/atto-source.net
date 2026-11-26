import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dotnet = spawnSync('dotnet', ['--version'], {encoding:'utf8'}).status === 0;
const main = 'fnc main(): int { return 42; }\n';

function check(source) {
  const directory = mkdtempSync(join(tmpdir(), 'atto-export-boundary-'));
  try {
    const path = join(directory, 'case.atto');
    writeFileSync(path, source, 'utf8');
    return spawnSync('dotnet', [
      'run', '--project', join(root, 'compiler', 'Ores.Net.Compiler'),
      '--', 'check', path
    ], {cwd:root, encoding:'utf8'});
  } finally {
    rmSync(directory, {recursive:true, force:true});
  }
}

test('native frontend rejects trailing package-only process entrypoint', {skip:!dotnet}, () => {
  const result = check(main + 'export pub[pkg] fnc main;\n');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr + result.stdout, /EXPORT_PACKAGE_LINKER_UNSUPPORTED/);
});

test('native frontend rejects contradictory public and package directives', {skip:!dotnet}, () => {
  for (const suffix of [
    'export pub fnc main;\nexport pub[pkg] fnc main;',
    'export pub[pkg] fnc main;\nexport pub fnc main;'
  ]) {
    const result = check(main + suffix + '\n');
    assert.notEqual(result.status, 0);
    assert.match(result.stderr + result.stdout, /EXPORT_/);
  }
});

test('identical checked main exports are idempotent in either position', {skip:!dotnet}, () => {
  for (const source of [
    'export pub main;\nexport pub fnc main;\n'+main,
    'export pub fnc main;\n'+main+'export pub main;\n',
    main+'export pub main;\nexport pub fnc main;\n'
  ]) {
    const result=check(source);
    assert.equal(result.status,0,result.stderr+result.stdout);
  }
});
test('raw export directive budget cannot be bypassed by duplicates', {skip:!dotnet}, () => {
  const result=check('export pub main;\n'.repeat(128)+main+'export pub main;\n');
  assert.notEqual(result.status,0);
  assert.match(result.stderr+result.stdout,/EXPORT_SOURCE_LIMIT/);
});
