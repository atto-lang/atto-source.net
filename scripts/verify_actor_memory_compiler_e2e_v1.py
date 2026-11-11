#!/usr/bin/env python3
"""Fail-closed, source-free validation; NOT a guest/compiler or zero-GC test."""
import json
from pathlib import Path
import sys

SCHEMA = "oreslang.actor_memory_compiler_e2e.v1"
SCOPE = ("Host substrate exercise is not compiler-lowered guest execution, "
         "NativeAOT, zero-GC, or low-GC certification")
HOST = (
    "persistent-owner-survives-turn",
    "temporary-owner-explicit-drop",
    "stale-handle-after-reuse-rejected",
    "foreign-actor-handle-rejected",
    "reused-storage-zeroed",
    "reclamation-after-quiescence",
)
GUEST = (
    "compiled-owned-object-allocation",
    "compiler-emitted-drop-on-all-exits",
    "persistent-state-through-suspend-resume",
    "scratch-revoked-on-turn-boundary",
    "cancel-and-error-cleanup",
    "ffi-pins-block-retirement",
    "actor-stop-releases-native-region",
    "no-guest-raw-pointer-escape",
    "actor-restart-rejects-stale-generation",
    "hot-path-allocation-metrics",
)
TARGETS = {
    "llvm": ("zero-tracing-gc", "tests/actor_memory_e2e_v1_host.c"),
    "net": ("low-clr-gc", "verification/Ores.Net.Tests/ActorMemoryE2EV1SelfTest.cs"),
    "java": ("low-jvm-gc", "src/test/java/dev/oreslang/runtime/ActorMemoryE2EV1Test.java"),
}
POLICY = {
    "exact_head_and_tree": True,
    "nonzero_executed_steps": True,
    "guest_compilation_and_execution": True,
    "native_memory_usage_measured": True,
    "managed_allocation_bytes_measured_on_net_and_java": True,
    "source_org_or_specifically_approved_private_runner": True,
    "zero_step_ci_is": "NOT_EXECUTED",
    "no_cross_org_private_source_export_without_approval": True,
}

class ContractError(ValueError):
    pass

def reject(msg):
    raise ContractError(msg)

def pairs_unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            reject("duplicate JSON key: " + key)
        result[key] = value
    return result

def validate(doc):
    if type(doc) is not dict or set(doc) != {
        "schema", "version", "scope", "host_checks", "guest_checks",
        "backend_targets", "evidence_requirements",
    }:
        reject("contract shape")
    if doc["schema"] != SCHEMA or type(doc["version"]) is not int or doc["version"] != 1:
        reject("schema/version mismatch")
    if doc["scope"] != SCOPE:
        reject("scope overclaimed")
    if doc["host_checks"] != list(HOST) or doc["guest_checks"] != list(GUEST):
        reject("missing/reordered conformance cases")
    if type(doc["backend_targets"]) is not dict or set(doc["backend_targets"]) != set(TARGETS):
        reject("backend inventory mismatch")
    for name, (goal, probe) in TARGETS.items():
        row = doc["backend_targets"][name]
        if type(row) is not dict or set(row) != {
            "goal", "host_probe", "host_verified", "compiled_guest", "aot",
            "allocation_measurement",
        }:
            reject("backend shape: " + name)
        if (row["goal"] != goal or row["host_probe"] != probe or
                row["host_verified"] is not False or
                row["compiled_guest"] != "NOT_EXECUTED" or
                row["aot"] != "NOT_EXECUTED" or
                row["allocation_measurement"] != "NOT_MEASURED"):
            reject("unverified result promoted: " + name)
    if type(doc["evidence_requirements"]) is not dict or doc["evidence_requirements"] != POLICY:
        reject("unsafe evidence policy")
    return doc

def validate_raw(raw):
    if type(raw) is not bytes or len(raw) == 0 or len(raw) > 65536:
        reject("invalid bytes")
    if raw.startswith(b"\xef\xbb\xbf"):
        reject("UTF-8 BOM forbidden")
    try:
        doc = json.loads(raw.decode("utf-8"), object_pairs_hook=pairs_unique,
                         parse_constant=lambda value: reject("nonfinite number: " + value))
    except (UnicodeError, ValueError, RecursionError) as exc:
        raise ContractError("invalid JSON") from exc
    return validate(doc)

if __name__ == "__main__":
    path = Path(sys.argv[1]) if len(sys.argv) == 2 else (
        Path(__file__).resolve().parents[1] / "contracts/v1/actor_memory_compiler_e2e_v1.json"
    )
    try:
        validate_raw(path.read_bytes())
    except (OSError, ContractError) as exc:
        print("ACTOR_MEMORY_COMPILER_E2E_V1_DENIED: " + str(exc), file=sys.stderr)
        raise SystemExit(1)
    print("ACTOR_MEMORY_COMPILER_E2E_V1_CONTRACT_PASS (policy-only; guest NOT_EXECUTED)")
