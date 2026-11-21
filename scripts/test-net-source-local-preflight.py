#!/usr/bin/env python3
"""Offline tests for the exact embedded GitHub Actions CIL admission preflight.

No network calls, GitHub token, source-export permissions, or dotnet required.
Runs the *actual Python heredoc extracted from the workflow*, not a copied
implementation, against synthetic REST responses with and without -O.
"""
import os
import subprocess
import sys
from pathlib import Path

WORKFLOW = Path(__file__).resolve().parents[1] / ".github/workflows/atto-net-approved-source-local.yml"
EXPECTED_CIL_HEAD = "850e46f2a02d3177c8b2f4a8b55a0be96893d09b"
MARKER = "          python3 - <<'PY'\n"
END = "\n          PY"

WRAPPER = r'''
import io
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
case = sys.argv[1]
expected = os.environ["SOURCE_HEAD"]
if case == "no_token":
    os.environ.pop("GH_REPO_READ_TOKEN", None)
else:
    os.environ["GH_REPO_READ_TOKEN"] = "nonsecret-synthetic-token"
def response(req, timeout=15):
    if case == "http_denied":
        raise urllib.error.HTTPError(req.full_url, 403, "Denied", {}, None)
    if req.full_url.endswith("/branches/main"):
        result = {"name": "main", "protected": case not in ("unprotected", "string_protected")}
        if case == "string_protected":
            result["protected"] = "true"
    elif req.full_url.endswith("/pulls/194"):
        result = {"number": 194, "head": {
            "sha": expected, "repo": {"full_name": "atto-lang/atto-source.net"}
        }}
        if case == "head_changed":
            result["head"]["sha"] = "0" * 40
        if case == "fork":
            result["head"]["repo"]["full_name"] = "untrusted/atto-source.net"
        if case == "pr_number":
            result["number"] = 193
        if case == "missing_repo":
            result["head"]["repo"] = None
    else:
        raise RuntimeError("Unexpected URL: " + req.full_url)
    return io.BytesIO(json.dumps(result).encode("utf-8"))
urllib.request.urlopen = response
exec(compile(Path(sys.argv[2]).read_text(), sys.argv[2], "exec"))
'''

SCENARIOS = (
    ("valid", True, "ATTO_SOURCE_LOCAL_PROTECTED_MAIN_CONFIRMED"),
    ("unprotected", False, "E_SOURCE_MAIN_NOT_PROTECTED"),
    ("string_protected", False, "E_SOURCE_MAIN_NOT_PROTECTED"),
    ("head_changed", False, "E_COMPILER_PR_SOURCE_DRIFT"),
    ("fork", False, "E_COMPILER_PR_SOURCE_DRIFT"),
    ("pr_number", False, "E_COMPILER_PR_SOURCE_DRIFT"),
    ("missing_repo", False, "E_COMPILER_PR_SOURCE_DRIFT"),
    ("no_token", False, "E_ADMISSION_NO_READ_TOKEN"),
    ("http_denied", False, "HTTP Error 403"),
)

def main():
    source = WORKFLOW.read_text(encoding="utf-8")
    if source.count(MARKER) != 1:
        raise RuntimeError("Expected exactly one embedded Python preflight")
    heredoc = source.split(MARKER, 1)[1]
    if END not in heredoc:
        raise RuntimeError("Missing embedded Python end delimiter")
    heredoc = heredoc.split(END, 1)[0]
    script = "\n".join(
        line[10:] if line.startswith("          ") else line
        for line in heredoc.split("\n")
    ) + "\n"
    if "assert " in script:
        raise RuntimeError("Security checks must not use Python assert")
    for diagnostic in ("E_ADMISSION_NO_READ_TOKEN", "E_SOURCE_MAIN_NOT_PROTECTED",
                       "E_COMPILER_PR_SOURCE_DRIFT"):
        if diagnostic not in script:
            raise RuntimeError("Missing fail-closed diagnostic " + diagnostic)
    if ("permissions:\n  contents: read\n  pull-requests: read\n") not in source:
        raise RuntimeError("Private PR preflight must request read-only PR metadata")
    if "\non:\n  workflow_dispatch:\n" not in source:
        raise RuntimeError("Manual source-local admission trigger missing")
    if "\n  pull_request:\n" in source or "\n  push:\n" in source:
        raise RuntimeError("Source-local CI must never trigger automatically")
    if "SOURCE_HEAD: " + EXPECTED_CIL_HEAD not in source:
        raise RuntimeError("Compiler source pin drift")
    import tempfile
    with tempfile.TemporaryDirectory(prefix="atto-source-local-preflight-") as temp:
        path = Path(temp) / "preflight.py"
        path.write_text(script, encoding="utf-8")
        cases = 0
        for optimized in (False, True):
            for scenario, success, receipt in SCENARIOS:
                cmd = [sys.executable]
                if optimized:
                    cmd.append("-O")
                cmd.extend(("-c", WRAPPER, scenario, str(path)))
                env = dict(os.environ, SOURCE_HEAD=EXPECTED_CIL_HEAD)
                result = subprocess.run(cmd, env=env, text=True,
                                        capture_output=True, timeout=10, check=False)
                output = result.stdout + result.stderr
                if ((result.returncode == 0) != success) or receipt not in output:
                    raise RuntimeError(
                        "Mock admission failed: scenario=%s optimized=%s code=%s log=%s"
                        % (scenario, optimized, result.returncode, output))
                cases += 1
    if cases != 18:
        raise RuntimeError("Expected 18 independent mode/scenario checks")
    print("ATTO_NET_SOURCE_LOCAL_PREFLIGHT_MOCK_PASS cases=18 optimized=true network=NONE")

if __name__ == "__main__":
    main()
