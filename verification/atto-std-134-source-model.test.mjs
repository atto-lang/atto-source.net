// Java/.NET backend-owned Atto std 134-vector SOURCE-ONLY test.
// This checks a restricted, audited policy-function subset, NOT a compiler,
// linked guest, Native Image/AOT, scheduler or filesystem capability.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, lstatSync } from "node:fs";
import { createHash } from "node:crypto";
import { runInNewContext } from "node:vm";

if (process.env.ATTO_REQUIRE_COMPILER_GUEST === "1" ||
    process.env.ATTO_REQUIRE_NATIVE_GUEST === "1") {
  throw Error("NATIVE_GUEST_NOT_EXECUTED: source-only std policy model cannot attest guest PASS");
}
const base = new URL("../", import.meta.url);
function strictSource(path) {
  const url = new URL(path, base);
  const metadata = lstatSync(url);
  assert.ok(metadata.isFile() && !metadata.isSymbolicLink(), "regular non-symlink source " + path);
  const bytes = readFileSync(url);
  assert.ok(bytes.length > 0 && bytes.length <= 1024 * 1024, "bounded source " + path);
  const text = new TextDecoder("utf-8", { fatal:true }).decode(bytes);
  assert.ok(text.endsWith("\n") && !text.includes("\0") &&
    !text.includes("\r") && !text.startsWith("\ufeff"), "strict UTF8/LF " + path);
  return { bytes, text };
}
function gitBlob(bytes) {
  return createHash("sha1").update(Buffer.from("blob " + bytes.length + "\0"))
    .update(bytes).digest("hex");
}
const contractFile = strictSource("contracts/atto-std-pure-policy-parity-v1.json");
const pinnedBlob = "3d9e9d5308a4cd0a99d811570f8c80e77b7219b7";
assert.equal(gitBlob(contractFile.bytes), pinnedBlob, "contract byte drift");
const contract = JSON.parse(contractFile.text);
assert.equal(contract.schema, "atto-std-pure-policy-parity-v1");
assert.equal(contract.vectors.length, 134);
assert.deepEqual(Object.keys(contract.api).sort(), ["path","sync","telemetry"]);
assert.equal(Object.values(contract.api).reduce((n, f) => n + Object.keys(f).length,0),28);
const files = Object.fromEntries(Object.keys(contract.api).map(
  key => [key,strictSource("stdlib/" + key + ".atto")]));
const deps = {
  path: ["is_separator","is_ascii_letter"],
  sync: ["mutex_acquire_admissible","mutex_queue_admissible",
    "rw_read_admissible","rw_write_admissible",
    "semaphore_acquire_admissible","semaphore_release_admissible",
    "barrier_is_last","generation_successor_admissible",
    "barrier_arrival_admissible"],
  telemetry: ["export_queue_admissible"]
};
function compileSubset(module,source) {
  const text = source.replace(/\/\/[^\n]*/g,"");
  const names = [...new Set([...Object.keys(contract.api[module]),...deps[module]])];
  const js = [], signatures = {};
  for (const name of names) {
    const pattern = new RegExp("\\b(?:pub\\s+)?fnc\\s+" + name +
      "\\s*\\(([^)]*)\\)\\s*:\\s*(bool|int)\\s*\\{", "g");
    const matches = [...text.matchAll(pattern)];
    assert.equal(matches.length, 1, module + "." + name + " must have one definition");
    const match = matches[0], start = match.index + match[0].length;
    if (Object.hasOwn(contract.api[module],name))
      assert.ok(match[0].startsWith("pub fnc "),module + "." + name + " public visibility");
    let depth = 1, end = start;
    for (; end < text.length && depth > 0; ++end) {
      if(text[end] === "{") ++depth;
      if(text[end] === "}") --depth;
    }
    assert.equal(depth,0,name + " closing function brace");
    const params = match[1].split(",").map(x => x.trim()).filter(Boolean).map(arg => {
      const typed=/^(bool|int)\s+([a-z_][a-z0-9_]*)$/.exec(arg);
      assert.ok(typed, "unsupported Atto argument " + name + " " + arg);
      return { type:typed[1], name:typed[2] };
    });
    const signature = [...params.map(x => x.type),match[2]];
    signatures[name] = signature;
    if (Object.hasOwn(contract.api[module],name))
      assert.deepEqual(signature,contract.api[module][name],
        module + "." + name + " public ABI");
    const body = text.slice(start,end - 1).replace(
      /if\s+([\s\S]*?)\s+then\s+return\s+([^;]+);\s*fi/g,
      (_,condition,result) => "if (" + condition.trim() + ") return " + result.trim() + ";");
    assert.doesNotMatch(body, /\b(?:fi|then|for|while|await|import|new|class|rt|asm|ffi)\b|[{}]/,
      module + "." + name + " out of source-only subset");
    const whitelist = new Set(["if","return","true","false",...params.map(x => x.name),...names]);
    for(const token of body.matchAll(/[a-z_][a-z0-9_]*/g))
      assert.ok(whitelist.has(token[0]),"untrusted reference " + module + "." + name + ":" + token[0]);
    assert.equal(body.replace(/[a-z_][a-z0-9_]*/g,"")
      .replace(/\d+/g,"").replace(/[+*\/%<>=!&|;(),\-\s]/g,""),"",
      module + "." + name + " unsupported grammar");
    js.push("function " + name + "(" + params.map(x => x.name).join(",") +
      "){" + body + "}");
  }
  return runInNewContext(js.join("\n") + "\n({" + names.join(",") + "})",
    Object.create(null),{ timeout:1500 });
}
function verify(source = files) {
  const api = Object.fromEntries(Object.keys(contract.api).map(name =>
    [name,compileSubset(name,source[name].text)]));
  for(const [index,vector] of contract.vectors.entries()) {
    assert.equal(vector.id,"STD-" + String(index+1).padStart(3,"0"),"closed IDs");
    const types = contract.api[vector.module]?.[vector.fn];
    assert.ok(types, vector.id + " public API");
    assert.equal(vector.args.length + 1,types.length,vector.id + " arity");
    for(let i=0;i<vector.args.length;i++) {
      assert.equal(typeof vector.args[i],types[i] === "bool" ? "boolean" : "number",vector.id);
      if(types[i] === "int") assert.ok(Number.isSafeInteger(vector.args[i]),vector.id + " numeric limit");
    }
    assert.equal(typeof vector.expected,types.at(-1) === "bool" ? "boolean" : "number",vector.id);
    const observed = api[vector.module][vector.fn](...vector.args);
    assert.equal(observed,vector.expected,vector.id +
      " " + vector.module + "." + vector.fn);
  }
  return contract.vectors.length;
}
test("all 28 backend-native Atto policy APIs match the 134 shared source vectors",()=>{
  assert.equal(verify(),134);
  console.log("ATTO_STD_134_SOURCE_MODEL_ONLY|guest=NOT_EXECUTED" +
    "|contract_blob=" + pinnedBlob);
});
test("five negative mutants cannot receive a source-model PASS",()=>{
  const mutated = (module,before,after) => {
    assert.ok(files[module].text.includes(before),"mutation anchor must exist");
    return { ...files,
      [module]: { text:files[module].text.replace(before,after) } };
  };
  assert.throws(()=>verify(mutated("path",
    "pub fnc is_safe_scalar","fnc is_safe_scalar")),"missing public API");
  assert.throws(()=>verify(mutated("path",
    "return scalar == 47 || (windows && scalar == 92);",
    "return false;")),"path separator inversion");
  assert.throws(()=>verify(mutated("sync",
    "return released <= capacity - available;",
    "return released >= 0;")),"permit over-release");
  assert.throws(()=>verify(mutated("telemetry",
    "return flags % 2 == 1;",
    "return flags % 2 == 0;")),"sampling bit inversion");
  assert.throws(()=>verify(mutated("telemetry",
    "pub fnc drop_increment(int count)",
    "java:host_call\npub fnc drop_increment(int count)")),"forbidden host injection");
});
test("guest fixture remains exact contract derivation, not guest evidence",()=>{
  const guest = strictSource("tests/fixtures/atto_std_pure_policy_guest_v1.atto").text;
  const literal = x => typeof x === "boolean" ? (x ? "true" : "false") : String(x);
  const expected = [
    "// CANDIDATE ONLY. The 134 checks below require a real trusted std",
    "// compiler-linker and native guest execution before claiming parity.",
    'import module path from "std/path";',
    'import module sync from "std/sync";',
    'import module Telemetry from "std/telemetry";',
    "",
    "pub fnc main(): int {",
    ...contract.vectors.map((v,i) => {
      const call = (v.module === "telemetry" ? "Telemetry" : v.module) +
        "." + v.fn + "(" + v.args.map(literal).join(", ") + ")";
      const bad = typeof v.expected === "boolean"
        ? (v.expected ? "!" + call : call)
        : call + " != " + literal(v.expected);
      return "  if " + bad + " then return " + (i + 1) + "; fi";
    }),
    "  return 0;",
    "}",
    ""
  ].join("\n");
  assert.equal(guest,expected,"guest behavior must track exact pinned JSON");
  assert.equal((guest.match(/\bthen return \d+; fi/g)||[]).length,134);
  assert.equal((guest.match(/\breturn 0;/g)||[]).length,1);
});
