import os
import sys
import unittest
from unittest.mock import patch

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import runners  # type: ignore


class _Proc:
    def __init__(self):
        self.returncode = 0
        self.stdout = "42\n"
        self.stderr = ""


class RunnerContainerModeTests(unittest.TestCase):
    def test_container_mode_builds_docker_command(self):
        captured = {}

        def fake_run(cmd, **kwargs):
            captured["cmd"] = cmd
            captured["kwargs"] = kwargs
            return _Proc()

        with patch.object(runners, "SANDBOX_RUNNER", True), \
             patch.object(runners, "SANDBOX_RUNNER_MODE", "container"), \
             patch.object(runners, "RUNNER_CONTAINER_RUNTIME", "docker"), \
             patch.object(runners, "RUNNER_CONTAINER_IMAGE", "taskforge-python-runner:wave4"), \
             patch.object(runners.shutil, "which", return_value="/usr/bin/docker"), \
             patch.object(runners.subprocess, "run", side_effect=fake_run):
            result = runners.run_python_solution("print(42)\n", "")

        self.assertEqual(result["returncode"], 0)
        self.assertEqual(result["sandboxMode"], "container")
        self.assertEqual(captured["cmd"][0:3], ["docker", "run", "--rm"])
        self.assertIn("--network", captured["cmd"])
        self.assertIn("taskforge-python-runner:wave4", captured["cmd"])


if __name__ == "__main__":
    unittest.main()
