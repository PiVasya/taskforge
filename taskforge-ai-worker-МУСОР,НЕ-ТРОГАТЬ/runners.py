"""Sandboxed code execution helpers."""

import os
import subprocess
import sys
import tempfile
from typing import Any, Dict, List

from text_utils import normalize_text, truncate_text


def run_python_solution(source_code: str, stdin_text: str) -> Dict[str, Any]:
    """Run a Python script in a temp directory and return stdout/stderr/rc."""
    with tempfile.TemporaryDirectory(prefix="tf-ai-") as tmp:
        path = os.path.join(tmp, "solution.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write(source_code)
        proc = subprocess.run(
            [sys.executable, path],
            input=stdin_text,
            text=True,
            capture_output=True,
            timeout=6,
        )
        return {
            "returncode": proc.returncode,
            "stdout": proc.stdout,
            "stderr": proc.stderr,
        }


def run_code_test_suite(source_code: str, tests: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Run *source_code* against a list of test dicts and summarise results."""
    results: List[Dict[str, Any]] = []
    passed = 0
    for idx, test in enumerate(tests, start=1):
        if not isinstance(test, dict):
            continue
        stdin_text = str(test.get("input") or "")
        expected = normalize_text(test.get("expectedOutput"))
        try:
            runtime = run_python_solution(source_code, stdin_text)
            actual = normalize_text(runtime.get("stdout"))
            ok = runtime.get("returncode") == 0 and actual == expected
            if ok:
                passed += 1
            results.append({
                "index": idx,
                "passed": ok,
                "returncode": runtime.get("returncode"),
                "actual": actual,
                "expected": expected,
                "stderr": truncate_text(runtime.get("stderr"), 180),
            })
        except Exception as ex:
            results.append({"index": idx, "passed": False, "error": str(ex)})
    return {"passed": passed, "total": len(results), "results": results}
