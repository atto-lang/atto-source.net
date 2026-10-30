// Verify byte-level content of independently downloaded and extracted GitHub
// Actions artifacts. This is a local consistency check, NOT a release approval.
import { createHash } from 'node:crypto';
import { closeSync, fstatSync, lstatSync, openSync, opendirSync, readFileSync } from 'node:fs';
import { open } from 'node:fs/promises';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { pathToFileURL } from 'node:url';
import { verifySourceLocalBundle } from './source-local-receipt-gate.mjs';

const linuxFiles = Object.freeze([
  'artifacts/compiler/linux-x64/oresnet',
  'artifacts/apps/linux-x64/oreslang-app',
  'artifacts/parity/linux-x64/oreslang-app',
  'artifacts/parity-precedence/linux-x64/oreslang-app',
  'artifacts/structured/linux-x64/oreslang-app',
]);
const windowsFiles = Object.freeze([
  'artifacts/compiler/win-x64/oresnet.exe',
  'artifacts/apps/win-x64/oreslang-app.exe',
]);
const receipts = Object.freeze({
  linux: 'artifacts/evidence/source-local-linux-sha256.txt',
  windows: 'artifacts/evidence/source-local-windows.json',
});
const hex64 = /^[0-9a-f]{64}$/;
const limitBytes = 512 * 1024 * 1024; // per native binary; fail closed on oversized inputs
const maxReceiptBytes = 64 * 1024; // prevent excessive untrusted receipt processing
const maxArtifactEntries = 128;
const maxArtifactDirectories = 64;

function enumerate(root) {
  const base = resolve(root);
  if (!lstatSync(base).isDirectory()) throw Error('artifact root is not a real directory');
  const found = new Set();
  let entriesVisited = 0, directoriesVisited = 0;
  const walk = dir => {
    const listing = opendirSync(dir);
    try {
      let entry;
      while ((entry = listing.readSync()) !== null) {
        if (++entriesVisited > maxArtifactEntries)
          throw Error('artifact has too many directory entries');
        const full = join(dir, entry.name);
        const key = relative(base, full).split(sep).join('/');
        if (entry.isSymbolicLink()) throw Error('artifact contains symlink: ' + key);
        if (entry.isDirectory()) {
          if (++directoriesVisited > maxArtifactDirectories)
            throw Error('artifact has too many directories');
          if (key.split('/').length > 8) throw Error('artifact nesting too deep');
          walk(full);
        } else if (entry.isFile()) {
          if (found.size >= 32) throw Error('artifact has too many files');
          found.add(key);
        } else throw Error('nonregular artifact entry: ' + key);
      }
    } finally {
      listing.closeSync();
    }
  };
  walk(base);
  return { base, found };
}
function assertArtifactParents(base, path) {
  const rel = relative(base, resolve(path));
  if (!rel || isAbsolute(rel) || rel === '..' || rel.startsWith('..' + sep))
    throw Error('artifact path escaped extraction root');
  const parts = rel.split(sep);
  let parent = base;
  if (!lstatSync(base).isDirectory()) throw Error('artifact root replaced');
  for (const part of parts.slice(0, -1)) {
    parent = join(parent, part);
    if (!lstatSync(parent).isDirectory())
      throw Error('artifact has non-directory or symlink ancestor: ' + parent);
  }
}
function exactFiles(found, expected) {
  // upload-artifact commonly strips the leading shared artifacts/ directory.
  // Require ONE uniform layout, never mixed prefixes or wildcard uploads.
  const canonical = [...expected].sort();
  const stripped = expected.map(p => p.slice('artifacts/'.length)).sort();
  const actual = [...found].sort();
  if (isDeepStrictEqual(actual, canonical)) return p => p;
  if (isDeepStrictEqual(actual, stripped)) return p => p.slice('artifacts/'.length);
  throw Error('artifact inventory differs from approved exact file allowlist');
}
// Bind all checks and reads to one opened file descriptor. Do not trust
// lstat(path) followed by a fresh, independent path-based createReadStream:
// an extracted file can be replaced between those two operations.
function assertStableFile(file, before, opened, maxBytes, minBytes) {
  if (!before.isFile() || !opened.isFile() ||
      before.nlink !== 1 || opened.nlink !== 1 ||
      opened.dev !== before.dev || opened.ino !== before.ino ||
      opened.size !== before.size ||
      opened.size < minBytes || opened.size > maxBytes)
    throw Error('invalid, linked, or replaced artifact file: ' + file);
}
async function digestAndHeader(file, base) {
  assertArtifactParents(base, file);
  const before = lstatSync(file);
  const handle = await open(file, 'r');
  try {
    const opened = await handle.stat();
    assertStableFile(file, before, opened, limitBytes, 64);
    const hash = createHash('sha256');
    let header = Buffer.alloc(0), readBytes = 0;
    for await (const chunk of handle.createReadStream({ highWaterMark: 65536, autoClose: false })) {
      readBytes += chunk.length;
      if (readBytes > limitBytes) throw Error('artifact binary exceeded byte limit: ' + file);
      if (header.length < 512)
        header = Buffer.concat([header, chunk.subarray(0, 512 - header.length)]);
      hash.update(chunk);
    }
    const after = await handle.stat();
    assertStableFile(file, before, after, limitBytes, 64);
    assertStableFile(file, before, lstatSync(file), limitBytes, 64);
    assertArtifactParents(base, file);
    if (readBytes !== before.size ||
        opened.mtimeMs !== after.mtimeMs || opened.ctimeMs !== after.ctimeMs)
      throw Error('artifact binary changed during read: ' + file);
    return { sha256: hash.digest('hex'), header };
  } finally {
    await handle.close();
  }
}
function assertFormat(header, os, path) {
  if (os === 'linux') {
    if (header.length < 64 || header.toString('hex', 0, 4) !== '7f454c46' ||
        header[4] !== 2 || header[5] !== 1 || ![2, 3].includes(header.readUInt16LE(16)) ||
        header.readUInt16LE(18) !== 62)
      throw Error('not x86-64 native ELF: ' + path);
    return;
  }
  if (header.length < 0x100 || header.toString('ascii', 0, 2) !== 'MZ')
    throw Error('not x86-64 native PE: ' + path);
  const offset = header.readUInt32LE(0x3c);
  if (offset < 0x40 || offset + 0xf8 > header.length ||
      header.toString('hex', offset, offset + 4) !== '50450000' ||
      header.readUInt16LE(offset + 4) !== 0x8664 ||
      header.readUInt16LE(offset + 20) < 240 ||
      header.readUInt16LE(offset + 24) !== 0x20b)
    throw Error('not x86-64 native PE: ' + path);
  // PE32+ data directory #14 is the CLR/COM descriptor. Native AOT must not
  // retain a managed CLR header, even if a file starts with MZ/PE signatures.
  const clr = offset + 24 + 112 + 14 * 8;
  if (clr + 8 > header.length || header.readUInt32LE(clr) !== 0 ||
      header.readUInt32LE(clr + 4) !== 0)
    throw Error('managed CLR descriptor present or PE truncated: ' + path);
}
// JSON.parse permits duplicate object keys. Reject duplicates (including
// escaped-equivalent names) before trusting a native Windows receipt.
function rejectRepeatedJsonKeys(raw) {
  let i = 0;
  const ws = () => { while (i < raw.length && /[\t\r\n ]/.test(raw[i])) i++; };
  const string = () => {
    const start = i;
    i++;
    let escaped = false;
    while (i < raw.length) {
      const ch = raw[i++];
      if (escaped) { escaped = false; continue; }
      if (ch === '\\') { escaped = true; continue; }
      if (ch === '"') return JSON.parse(raw.slice(start, i));
    }
    throw Error('unterminated receipt JSON string');
  };
  const value = depth => {
    if (depth > 32) throw Error('receipt JSON nested too deeply');
    ws();
    if (raw[i] === '{') {
      i++; ws(); const keys = new Set();
      if (raw[i] !== '}') for (;;) {
        ws();
        if (raw[i] !== '"') throw Error('bad receipt JSON object key');
        const key = string();
        if (keys.has(key)) throw Error('duplicate receipt JSON key: ' + key);
        keys.add(key); ws();
        if (raw[i++] !== ':') throw Error('bad receipt JSON property');
        value(depth + 1); ws();
        if (raw[i] === '}') break;
        if (raw[i++] !== ',') throw Error('bad receipt JSON object');
      }
      i++;
    } else if (raw[i] === '[') {
      i++; ws();
      if (raw[i] !== ']') for (;;) {
        value(depth + 1); ws();
        if (raw[i] === ']') break;
        if (raw[i++] !== ',') throw Error('bad receipt JSON array');
      }
      i++;
    } else if (raw[i] === '"') string();
    else {
      const m = raw.slice(i).match(/^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/);
      if (!m) throw Error('bad receipt JSON scalar');
      i += m[0].length;
    }
  };
  value(0); ws();
  if (i !== raw.length) throw Error('trailing receipt JSON content');
}

function expectedHashes(bundle, kind) {
  if (kind === 'windows') {
    const bins = bundle.receipts.windows.binaries;
    if (!Array.isArray(bins) || bins.length !== windowsFiles.length)
      throw Error('Windows receipt lacks native binary digests');
    return new Map(bins.map(x => [x.path, x.sha256]));
  }
  const result = new Map();
  for (const line of bundle.receipts.linux.trimEnd().split(/\r?\n/)) {
    const m = line.match(/^([0-9a-f]{64})  (artifacts\/[a-zA-Z0-9./_-]+)$/);
    if (m) {
      if (result.has(m[2])) throw Error('duplicate Linux binary digest');
      result.set(m[2], m[1]);
    }
  }
  return result;
}
export async function verifyArtifactBytes(bundle, roots) {
  const errors = verifySourceLocalBundle(bundle);
  if (errors.length) throw Error('metadata admission refused: ' + errors.join('; '));
  for (const kind of ['linux', 'windows']) {
    const expected = kind === 'linux' ? linuxFiles : windowsFiles;
    const {base, found} = enumerate(roots[kind]);
    const mapPath = exactFiles(found, [...expected, receipts[kind]]);
    const read = path => {
      const receipt = join(base, mapPath(path));
      assertArtifactParents(base, receipt);
      const before = lstatSync(receipt);
      if (!before.isFile() || before.size < 1 || before.size > maxReceiptBytes)
        throw Error('oversized or invalid artifact receipt');
      const fd = openSync(receipt, 'r');
      try {
        const opened = fstatSync(fd);
        assertStableFile(receipt, before, opened, maxReceiptBytes, 1);
        const bytes = readFileSync(fd);
        const after = fstatSync(fd);
        assertStableFile(receipt, before, after, maxReceiptBytes, 1);
        assertStableFile(receipt, before, lstatSync(receipt), maxReceiptBytes, 1);
        assertArtifactParents(base, receipt);
        if (bytes.length !== before.size ||
            opened.mtimeMs !== after.mtimeMs || opened.ctimeMs !== after.ctimeMs)
          throw Error('artifact receipt changed during read');
        return bytes;
      } finally {
        closeSync(fd);
      }
    };
    if (kind === 'linux') {
      if (read(receipts[kind]).toString('utf8') !== bundle.receipts.linux)
        throw Error('Linux artifact receipt differs from independently acquired metadata');
    } else {
      let parsed;
      const raw = read(receipts[kind]).toString('utf8').replace(/^\uFEFF/, '');
      try { parsed = JSON.parse(raw); }
      catch { throw Error('Windows artifact receipt is not valid JSON'); }
      rejectRepeatedJsonKeys(raw);
      if (!isDeepStrictEqual(parsed, bundle.receipts.windows))
        throw Error('Windows artifact receipt differs from independently acquired metadata');
    }
    const hashes = expectedHashes(bundle, kind);
    if (!isDeepStrictEqual([...hashes.keys()].sort(), [...expected].sort()))
      throw Error('native hash inventory mismatch: ' + kind);
    if (new Set([...hashes.values()]).size !== expected.length)
      throw Error('duplicate SHA-256 across independent native executables: ' + kind);
    for (const path of expected) {
      const wanted = hashes.get(path);
      if (!hex64.test(wanted)) throw Error('invalid digest in receipt: ' + path);
      const {sha256, header} = await digestAndHeader(join(base, mapPath(path)), base);
      assertFormat(header, kind, path);
      if (sha256 !== wanted) throw Error('native binary SHA-256 mismatch: ' + path);
    }
  }
  return { linuxBinaries: linuxFiles.length, windowsBinaries: windowsFiles.length };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (process.argv.length !== 5)
      throw Error('usage: node source-local-artifact-byte-gate.mjs bundle.json linux-extracted-dir windows-extracted-dir');
    const bundle = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    const result = await verifyArtifactBytes(bundle, { linux: process.argv[3], windows: process.argv[4] });
    console.log('SOURCE_LOCAL_ARTIFACT_BYTES_MATCH_RECEIPTS (' +
      result.linuxBinaries + ' ELF, ' + result.windowsBinaries + ' PE; NOT RELEASE_CERTIFIED)');
  } catch (error) {
    console.error('DENIED: ' + error.message);
    process.exitCode = 1;
  }
}
