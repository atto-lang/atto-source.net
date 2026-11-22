#!/usr/bin/env node
// Backend-owned Atto stdlib SOURCE admission; NEVER a compiler/NativeAOT guest receipt.
import { readFileSync, lstatSync } from "node:fs";
import { execFileSync } from "node:child_process";

const expected = {
  path: "stdlib/path.atto",
  sync: "stdlib/sync.atto",
  Telemetry: "stdlib/telemetry.atto",
};
const fixturePath = "tests/fixtures/path_sync_telemetry_policy_v1.atto";
const maxBytes = 1_048_576;
const sourceFiles = [...Object.values(expected), fixturePath];

function fail(message) { throw new Error("ATTO_STDLIB_SOURCE_REJECTED: " + message); }

function readSource(file) {
  const stat = lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink()) fail(file + " not a regular file");
  const bytes = readFileSync(file);
  if (bytes.length === 0 || bytes.length > maxBytes) fail(file + " invalid length");
  const value = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  if (value.includes("\u0000") || value.includes("\r") || value.charCodeAt(0) === 0xfeff)
    fail(file + " NUL/CR/BOM");
  if (!value.endsWith("\n")) fail(file + " missing terminal LF");
  return value;
}
function check(inputs, guest) {
  const names = Object.keys(expected);
  const exports = {};
  for (const name of names) {
    const src = inputs[name];
    if (!src.includes(`define module ${name} as`))
      fail("module identity mismatch " + name);
    if (/java:|do-java|csharp:|\bReflection\.Emit\b/.test(src))
      fail("host island in " + name);
    exports[name] = new Set(
      [...src.matchAll(/\bpub\s+fnc\s+([a-z][a-z0-9_]*)\s*\(/g)]
        .map(match => match[1]));
  }
  if (!inputs.path.includes("pub define class Path as"))
    fail("native Path value class not exposed");
  if (!inputs.path.includes("normalized_component_start_points") ||
      !inputs.sync.includes("mutex_release_receipt_admissible") ||
      !inputs.Telemetry.includes("traceparent_v00_header_valid"))
    fail("security policy anchor missing");
  if (!guest.includes('import path from "../../stdlib/path.atto"') ||
      !guest.includes('import sync from "../../stdlib/sync.atto"') ||
      !guest.includes('import Telemetry from "../../stdlib/telemetry.atto"'))
    fail("fixture does not use independent backend-local .atto imports");
  let calls = 0;
  for (const match of guest.matchAll(/\b(path|sync|Telemetry)\.([a-z][a-z0-9_]*)\s*\(/g)) {
    if (!exports[match[1]].has(match[2]))
      fail("missing public native .atto function " + match[1] + "." + match[2]);
    calls++;
  }
  if (calls < 40) fail("fixture public-function coverage unexpectedly low");
  const pathBlock = inputs.path.slice(inputs.path.indexOf("pub define class Path as"));
  const methods = new Set([...pathBlock.matchAll(/\bpub\s+fnc\s+([a-z][a-z0-9_]*)\s*\(/g)]
    .map(match => match[1]));
  const required = ["component_count", "component_start", "component_length",
    "component_scalar", "file_name_offset", "file_name_size", "extension_offset",
    "stem_length", "parent_component_count", "is_canonical", "is_portable", "can_join"];
  for (const method of required) {
    if (!methods.has(method) || !guest.includes("item." + method + "(") &&
        !guest.includes("clean." + method + "("))
      fail("missing tested native Path member " + method);
  }
  const codes = [...guest.matchAll(/\bthen return\s+(\d+);\s*fi/g)]
    .map(match => Number(match[1]));
  if (codes.length !== 65 || codes.some((value, index) => value !== index + 1))
    fail("65 sequential nonzero guest failure exits are required");
  if (!/\breturn 0;\s*}\s*$/.test(guest))
    fail("guest must reach exactly one terminal zero status");
  return { caseCount: codes.length, nativeCalls: calls, nativePathMethods: required.length };
}

function rejectMutation(name, inputs, guest) {
  try { check(inputs, guest); }
  catch (error) {
    if (String(error.message).includes("ATTO_STDLIB_SOURCE_REJECTED:"))
      return;
    throw error;
  }
  throw new Error("source-admission accepted mutated case: " + name);
}

const sources = Object.fromEntries(
  Object.entries(expected).map(([name, file]) => [name, readSource(file)]));
const guest = readSource(fixturePath);
const valid = check(sources, guest);

if (process.argv.includes("--require-executed-guest"))
  fail("SOURCE_ONLY: this checker cannot certify linked guest or NativeAOT execution");

if (!process.argv.includes("--self-test")) fail("use --self-test for source-only verification");
rejectMutation("nonpublic .atto method",
  { ...sources, path: sources.path.replace("pub fnc normalized_windows_depth", "fnc normalized_windows_depth") }, guest);
rejectMutation("nonpublic Path class",
  { ...sources, path: sources.path.replace("pub define class Path as", "define class Path as") }, guest);
rejectMutation("host island",
  { ...sources, path: sources.path + "\njava:untrusted\n" }, guest);
rejectMutation("case count",
  sources, guest.replace("then return 1; fi", "then return 999; fi"));

for (const file of sourceFiles) {
  const workingSha = execFileSync("git", ["hash-object", "--no-filters", "--", file],
    { encoding: "utf8" }).trim();
  const treeSha = execFileSync("git", ["rev-parse", "HEAD:" + file],
    { encoding: "utf8" }).trim();
  if (workingSha !== treeSha) fail("working file differs from exact Git tree: " + file);
}
console.log(`ATTO_STDLIB_NET_SOURCE_AUDIT=CHECKED cases=${valid.caseCount} calls=${valid.nativeCalls} path_methods=${valid.nativePathMethods} mutation_negatives=4`);
console.log("ATTO_STDLIB_NET_LINKED_GUEST=NOT_EXECUTED");
console.log("ATTO_STDLIB_NET_NATIVE_AOT=NOT_EXECUTED");
