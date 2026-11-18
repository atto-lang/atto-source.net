#!/usr/bin/env python3
"""Offline structural gates for the .NET Native AOT + Wasmtime prototype.

These tests inspect checked-out source only. They do NOT certify compilation,
a functioning native binary, execution of untrusted guests or a security
sandbox. Run a real published Native AOT binary as a separate release gate.
"""
from __future__ import annotations

import json
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNNER = "verification/Ores.Net.WasmtimeAotRunner"
BUILDER = "verification/Ores.Net.WasmtimeAotBuilder"
PROBE = "verification/Ores.Net.WasmtimeProbe"
PROFILE = "fuel-stack262144-mem64off-reftypesoff-multimemoryoff-threadsoff"
VERSION = "50.0.1"  # known affected version: synthetic-only; release must not promote it


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def project(path: str) -> ET.Element:
    return ET.fromstring(read(path))


class StrictFullAotSourceContract(unittest.TestCase):
    def test_all_experimental_binaries_require_native_aot(self) -> None:
        for name in (RUNNER, BUILDER, PROBE):
            with self.subTest(name=name):
                # Keep exact file names explicit; accidentally testing a
                # different project should not pass.
                filename = {
                    RUNNER: "Ores.Net.WasmtimeAotRunner.csproj",
                    BUILDER: "Ores.Net.WasmtimeAotBuilder.csproj",
                    PROBE: "Ores.Net.WasmtimeProbe.csproj",
                }[name]
                root = project(f"{name}/{filename}")
                for key in ("PublishAot", "SelfContained", "IsAotCompatible"):
                    self.assertEqual(root.findtext(f"./PropertyGroup/{key}"), "true")
                ref = root.find("./ItemGroup/PackageReference[@Include='Wasmtime']")
                self.assertIsNotNone(ref)
                self.assertEqual(ref.attrib["Version"], VERSION)
                self.assertIn('StrictNativeAot.targets', read(f"{name}/{filename}"))

    def test_precompiled_runtime_does_not_compile_guest(self) -> None:
        for source in (read(f"{RUNNER}/Program.cs"),
                       read(f"{RUNNER}/TrustedAotArtifact.cs")):
            self.assertIsNone(re.search(
                r"\\bModule\\.(FromText|FromBytes|FromFile|FromStream|"
                r"FromTextFile|FromTextStream|ConvertText|Validate)\\s*\\(", source))
            self.assertNotIn("Module.DeserializeFile(", source)
            self.assertNotIn(".Serialize(", source)

    def test_compilation_is_in_trusted_builder_only(self) -> None:
        builder = read(f"{BUILDER}/Program.cs")
        self.assertIn("Module.FromText(engine", builder)
        self.assertIn("module.Serialize()", builder)
        runtime = read(f"{RUNNER}/Program.cs")
        self.assertIn('Module.Deserialize(engine, "signed-synthetic-ci-guest", artifact)', runtime)
        self.assertLess(runtime.index("TrustedAotArtifact.Verify("),
                        runtime.index("Module.Deserialize("))

    def test_engine_profile_signed_and_agreed(self) -> None:
        builder = read(f"{BUILDER}/Program.cs")
        runner = read(f"{RUNNER}/Program.cs")
        verifier = read(f"{RUNNER}/TrustedAotArtifact.cs")
        self.assertIn(PROFILE, builder)
        self.assertIn(PROFILE, verifier)
        for source in (builder, runner, read(f"{PROBE}/Program.cs")):
            for line in (".WithFuelConsumption(true)",
                         ".WithMemory64(false)",
                         ".WithReferenceTypes(false)",
                         ".WithMultiMemory(false)",
                         ".WithWasmThreads(false)"):
                self.assertIn(line, source)

    def test_aot_signature_verified_before_native_code_load(self) -> None:
        runner = read(f"{RUNNER}/Program.cs")
        gate = read(f"{RUNNER}/TrustedAotArtifact.cs")
        self.assertIn("RSA.Create(3072)", runner)  # wrong-signer negative test
        self.assertLess(gate.index("verifier.VerifyData("),
                        gate.index("JsonDocument.Parse("))
        self.assertIn("RSASignaturePadding.Pss", gate)
        self.assertIn("FixedTimeEquals(approvedDigest, actualDigest)", gate)
        self.assertIn("!seen.Add(property.Name)", gate)
        self.assertIn('guest_kind").GetString() != "synthetic-ci-only"', gate)
        self.assertIn("MaxArtifactBytes = 16 * 1024 * 1024", gate)
        self.assertIn("Module.Deserialize(", runner)

    def test_adversarial_negative_cases_stay_wired(self) -> None:
        runner = read(f"{RUNNER}/Program.cs")
        for case in ("swappedArtifact", "changedManifest", "badSignature",
                     "wrongIssuer", "duplicated signed JSON key",
                     "invalid compiler source identity", "memory out-of-bounds",
                     "fuel exhaustion", "neighbor survived"):
            self.assertIn(case, runner)

    def test_untrusted_runtime_admission_remains_denied(self) -> None:
        source = read("runtime/Ores.Net.Runtime/OresVmHost.cs")
        marker = "if (isolation == IsolationMode.WasmtimeSandbox)"
        self.assertIn(marker, source)
        self.assertIn("throw new InvalidOperationException", source[
            source.index(marker):source.index(marker)+200])
        self.assertIn("if (isolation != IsolationMode.TrustedInProcess)", source)
        self.assertIn("WasmtimeSandbox", read(
            "verification/Ores.Net.Tests/WasmtimeTenantAdmissionTests.cs"))

    def test_windows_cet_block_is_not_disabled(self) -> None:
        for name in (RUNNER, PROBE):
            source = read(f"{name}/Program.cs")
            self.assertIn("OperatingSystem.IsWindows()", source)
            self.assertIn("return 78", source)
            self.assertNotIn("/CETCOMPAT:NO", source)
            self.assertNotIn("SetProcessMitigationPolicy", source)

    def test_private_transfer_stays_deny_by_default(self) -> None:
        policy = json.loads(read("contracts/private-ci-security.v1.json"))
        transfer = policy["source_transfer"]
        for key in ("default_deny", "private_destination_only",
                    "require_destination_approval_on_protected_source_main",
                    "require_independent_security_code_owner_review",
                    "require_exact_source_commit_and_complete_git_tree",
                    "require_destination_blob_tree_commit_ref_attestation",
                    "never_infer_approval_from_actions_minutes"):
            self.assertIs(transfer[key], True)

    def test_workflows_pin_same_repo_and_exact_sha(self) -> None:
        for workflow in ("wasmtime-strict-aot.yml", "wasmtime-nativeaot-probe.yml"):
            with self.subTest(workflow=workflow):
                source = read(".github/workflows/" + workflow)
                self.assertIn("1419704375", source)
                self.assertIn("test \"$PR_HEAD_REPO_ID\" = '1419704375'", source)
                self.assertIn("persist-credentials: false", source)
                self.assertIn("git rev-parse HEAD", source)
                self.assertIn("11bd71901bbe5b1630ceea73d27597364c9af683", source)
                self.assertIn("67a3573c9a986a3f9c594539f4ab511d57bb3ce9", source)

    def test_strict_workflow_requires_executed_receipts(self) -> None:
        source = read(".github/workflows/wasmtime-strict-aot.yml")
        for token in ("NET_WASMTIME_TRUSTED_AOT_BUILDER_PASS",
                      "NET_WASMTIME_STRICT_AOT_RUNTIME_PASS",
                      "-p:PublishAot=true", "ELF 64-bit"):
            self.assertIn(token, source)


if __name__ == "__main__":
    unittest.main(verbosity=2)
