import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import preflight  # type: ignore


class TestPreflight(unittest.TestCase):
    def test_missing_internal_key_is_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            with patch.dict(os.environ, {"TASKFORGE_AI_LOG_DIR": tmp}, clear=False), patch.object(preflight, "API_KEY", ""):
                report = preflight.build_report()
                codes = {item["code"] for item in report["errors"]}
                self.assertIn("internal_key_missing", codes)
                self.assertFalse(report["ok"])

    def test_container_sandbox_requires_docker(self):
        with tempfile.TemporaryDirectory() as tmp:
            with (
                patch.dict(os.environ, {"TASKFORGE_AI_LOG_DIR": tmp}, clear=False),
                patch.object(preflight, "SANDBOX_RUNNER", True),
                patch.object(preflight, "SANDBOX_RUNNER_MODE", "container"),
                patch.object(preflight, "RUNNER_CONTAINER_RUNTIME", "docker"),
                patch("preflight.shutil.which", return_value=None),
            ):
                report = preflight.build_report()
                codes = {item["code"] for item in report["errors"]}
                self.assertIn("docker_missing", codes)

    def test_writable_log_dir_passes_core_checks(self):
        with tempfile.TemporaryDirectory() as tmp:
            with (
                patch.dict(os.environ, {"TASKFORGE_AI_LOG_DIR": tmp}, clear=False),
                patch.object(preflight, "API_KEY", "secret"),
                patch.object(preflight, "EXTERNAL_AI_API_KEY", "ext-secret"),
                patch.object(preflight, "SANDBOX_RUNNER", False),
            ):
                report = preflight.build_report()
                codes = {item["code"] for item in report["errors"]}
                self.assertNotIn("log_dir_unwritable", codes)


if __name__ == "__main__":
    unittest.main()
