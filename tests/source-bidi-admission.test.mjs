import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, writeFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join, resolve} from 'node:path';
import {spawnSync} from 'node:child_process';

const project = resolve('compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj');
const bidiControls = [0x061c, 0x200e, 0x200f, 0x202a, 0x202b, 0x202c,
  0x202d, 0x202e, 0x2066, 0x2067, 0x2068, 0x2069];

test('real .NET CLI check rejects source bidi controls, including comments', async t => {
  if (spawnSync('dotnet', ['--version'], {encoding:'utf8'}).status !== 0) {
    t.skip('dotnet toolchain unavailable; no compiler-execution claim');
    return;
  }
  const directory = await mkdtemp(join(tmpdir(), 'ores-net-bidi-'));
  const sourcePath = join(directory, 'source.atto');
  const safe = 'pub fnc main(): int { return 42; }';
  try {
    // Build once; each negative probe runs the production CLI entry point.
    const build = spawnSync('dotnet', ['build', project, '-o', directory], {
      encoding:'utf8', timeout:120000
    });
    assert.equal(build.status, 0, build.stderr || build.stdout);
    const dll = join(directory, 'Ores.Net.Compiler.dll');
    for (const cp of bidiControls) {
      for (const value of [safe + ' // ', '/* ']) {
        const text = value.startsWith('/* ')
          ? value + String.fromCodePoint(cp) + ' */ ' + safe
          : value + String.fromCodePoint(cp);
        await writeFile(sourcePath, text);
        const result = spawnSync('dotnet', [dll, 'check', sourcePath], {
          encoding:'utf8', timeout:15000
        });
        assert.notEqual(result.status, 0, 'admitted U+' + cp.toString(16));
        assert.match(result.stderr, /Source contains bidirectional Unicode control/);
      }
    }
  } finally {
    await rm(directory, {recursive:true, force:true});
  }
});
