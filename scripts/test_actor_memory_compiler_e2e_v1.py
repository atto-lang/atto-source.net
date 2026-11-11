#!/usr/bin/env python3
"""Negative controls for actor-memory E2E v1 claims (source-free only)."""
import copy
from pathlib import Path
import unittest

from verify_actor_memory_compiler_e2e_v1 import ContractError, validate, validate_raw

DATA = (Path(__file__).resolve().parents[1] /
        "contracts/v1/actor_memory_compiler_e2e_v1.json")

class ActorMemoryE2EV1ContractTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.base = validate_raw(DATA.read_bytes())

    def reject(self, mutate):
        doc = copy.deepcopy(self.base)
        mutate(doc)
        with self.assertRaises(ContractError):
            validate(doc)

    def test_original(self):
        self.assertEqual(validate(self.base), self.base)

    def test_duplicate_key(self):
        with self.assertRaises(ContractError):
            validate_raw(b'{"schema":1,"schema":2}')

    def test_bom(self):
        with self.assertRaises(ContractError):
            validate_raw(b"\xef\xbb\xbf" + DATA.read_bytes())

    def test_invalid_utf8(self):
        with self.assertRaises(ContractError):
            validate_raw(b"\xff")

    def test_missing_backend(self):
        self.reject(lambda d: d["backend_targets"].pop("net"))

    def test_unknown_backend(self):
        self.reject(lambda d: d["backend_targets"].update({"forge": {}}))

    def test_compiled_guest_false_pass(self):
        self.reject(lambda d: d["backend_targets"]["llvm"].update(
            {"compiled_guest": "PASS"}))

    def test_aot_false_pass(self):
        self.reject(lambda d: d["backend_targets"]["net"].update(
            {"aot": "PASS"}))

    def test_host_false_pass(self):
        self.reject(lambda d: d["backend_targets"]["java"].update(
            {"host_verified": True}))

    def test_managed_alloc_false_pass(self):
        self.reject(lambda d: d["backend_targets"]["java"].update(
            {"allocation_measurement": "ZERO"}))

    def test_zero_step_ci_pass(self):
        self.reject(lambda d: d["evidence_requirements"].update(
            {"zero_step_ci_is": "PASS"}))

    def test_mirror_without_approval(self):
        self.reject(lambda d: d["evidence_requirements"].update(
            {"no_cross_org_private_source_export_without_approval": False}))

    def test_host_case_missing(self):
        self.reject(lambda d: d["host_checks"].pop())

    def test_guest_case_missing(self):
        self.reject(lambda d: d["guest_checks"].pop())

    def test_guest_case_reordered(self):
        self.reject(lambda d: d["guest_checks"].reverse())

    def test_scope_promoted(self):
        self.reject(lambda d: d.update({"scope": "fully certified"}))

    def test_changed_probe_path(self):
        self.reject(lambda d: d["backend_targets"]["net"].update(
            {"host_probe": "tests/fake.cs"}))

    def test_unknown_top_level_field(self):
        self.reject(lambda d: d.update({"unreviewed": 1}))

    def test_boolean_version_rejected(self):
        self.reject(lambda d: d.update({"version": True}))

if __name__ == "__main__":
    unittest.main()
