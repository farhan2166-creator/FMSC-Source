"""Exercise the real Workflow 04 auto-retry job guard against synthetic GitHub job responses.

This test does not contact GitHub, dispatch releases, or touch application/SQL data.
On Ubuntu security CI jq is required; Windows portable discovery may skip when absent.
"""
import copy
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parent.parent
WORKFLOW = ROOT / ".github/workflows/fmsc-workflow04-auto-retry.yml"
SAFE = "Preflight, prepare or reuse next patch-version Source commit"
GATE = "Require NEW private SQL, real installer and chaos acceptance"
PACKAGE = "Verify and package Windows x64"
PUBLISH = "Publish approved private update"


def retry_filter():
    yaml = WORKFLOW.read_text(encoding="utf-8")
    # A different jq guard checks the source/main commit comparison above.
    # Match one complete shell-quoted expression, not text between two jq calls.
    marker = re.search(r"""if ! jq -e '([^']*)' <<<"\$jobs" >/dev/null; then""",
                       yaml, re.DOTALL)
    if not marker:
        raise AssertionError("Missing fail-closed job evidence check in retry workflow")
    return marker.group(1)


def response():
    return {
        "total_count": 4,
        "jobs": [
            {"name": SAFE, "status": "completed", "conclusion": "failure",
             "steps": [{"name": "Owner preflight", "conclusion": "failure"}]},
            {"name": GATE, "status": "completed", "conclusion": "skipped",
             "steps": []},
            {"name": PACKAGE, "status": "completed", "conclusion": "skipped",
             "steps": []},
            {"name": PUBLISH, "status": "completed", "conclusion": "skipped",
             "steps": []},
        ]
    }


@unittest.skipUnless(shutil.which("jq") or os.environ.get("FMSC_REQUIRE_JQ") == "1",
                     "jq is not installed on this development machine")
class Workflow04SafeRetryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("jq"):
            raise RuntimeError("Required jq is missing on security CI")
        cls.expression = retry_filter()

    def allowed(self, jobs):
        run = subprocess.run(["jq", "-e", self.expression], input=json.dumps(jobs),
                             capture_output=True, text=True, timeout=10)
        if run.returncode not in (0, 1, 4):
            self.fail("Retry jq expression failed unexpectedly: " + run.stderr)
        return run.returncode == 0

    def test_safe_retry_before_package(self):
        self.assertTrue(self.allowed(response()))
        candidate = response()
        candidate["jobs"][0]["steps"][0]["name"] = "Preflight renamed by maintainer"
        self.assertTrue(self.allowed(candidate),
                        "Step caption changes must not affect retry safety")
        candidate = response()
        candidate["jobs"][0]["conclusion"] = "success"
        candidate["jobs"][1]["conclusion"] = "failure"
        candidate["jobs"][1]["steps"] = [{"name": "Sandbox acceptance failed", "conclusion": "failure"}]
        self.assertTrue(self.allowed(candidate), "Safe Sandbox-gate failures remain retryable")

    def test_safe_retry_after_nonpublishing_package_failure(self):
        candidate = response()
        candidate["jobs"][0]["conclusion"] = "success"
        candidate["jobs"][2]["conclusion"] = "failure"
        candidate["jobs"][2]["steps"] = [
            {"name": "Build verification failed", "conclusion": "failure"}]
        self.assertTrue(self.allowed(candidate),
                        "A failing build before publisher execution must remain retryable")
        candidate["jobs"][2]["conclusion"] = "success"
        candidate["jobs"][2]["steps"][0]["conclusion"] = "success"
        candidate["jobs"][1]["conclusion"] = "failure"
        self.assertTrue(self.allowed(candidate),
                        "A completed package cannot authorize publication or block safe retry")

    def test_refuse_any_possible_publisher_execution(self):
        for conclusion in ("success", "failure", "cancelled", None):
            with self.subTest(conclusion=conclusion):
                candidate = response()
                candidate["jobs"][3]["conclusion"] = conclusion
                candidate["jobs"][3]["steps"] = [
                    {"name": "Completely renamed publication", "conclusion": conclusion}]
                self.assertFalse(self.allowed(candidate))
        candidate = response()
        candidate["jobs"][3]["steps"] = [
            {"name": "Renamed publisher", "conclusion": "success"}]
        self.assertFalse(self.allowed(candidate),
                         "A skipped job with an executed step is contradictory, not approval")

    def test_refuse_missing_ambiguous_or_future_jobs(self):
        for mutate in (
            lambda v: v.pop("jobs"),
            lambda v: v.pop("total_count"),
            lambda v: v.update(total_count=5),
            lambda v: v["jobs"].pop(),
            lambda v: v["jobs"][3].update(name="New publication job label"),
            lambda v: v["jobs"].append({"name": "Extra potentially publishing job",
                                        "conclusion": "failure"}),
            lambda v: v["jobs"][1].update(name=v["jobs"][0]["name"]),
        ):
            with self.subTest(mutate=mutate):
                candidate = response()
                mutate(candidate)
                self.assertFalse(self.allowed(candidate),
                                 "Unrecognized/partial execution evidence cannot authorize retry")


if __name__ == "__main__":
    unittest.main()
