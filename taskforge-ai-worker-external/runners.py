"""Sandboxed code execution helpers."""

from __future__ import annotations

import os
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
from typing import Any, Dict, List

from config import (
    RUNNER_CONTAINER_IMAGE,
    RUNNER_CONTAINER_NETWORK,
    RUNNER_CONTAINER_NO_NEW_PRIVS,
    RUNNER_CONTAINER_PIDS_LIMIT,
    RUNNER_CONTAINER_RUNTIME,
    RUNNER_CONTAINER_TMPFS_MB,
    RUNNER_CPU_SECONDS,
    RUNNER_MEMORY_MB,
    RUNNER_STDERR_LIMIT,
    RUNNER_STDOUT_LIMIT,
    RUNNER_TIMEOUT_SECONDS,
    SANDBOX_RUNNER,
    SANDBOX_RUNNER_MODE,
)
from text_utils import normalize_text, truncate_text


def _truncate_stream(value: str, limit: int) -> str:
    if limit <= 0:
        return value
    return value if len(value) <= limit else value[: limit - 3] + "..."


def _sandbox_preexec() -> None:
    if not SANDBOX_RUNNER:
        return
    os.setsid()
    try:
        import resource

        cpu = max(1, RUNNER_CPU_SECONDS)
        mem_bytes = max(64, RUNNER_MEMORY_MB) * 1024 * 1024
        resource.setrlimit(resource.RLIMIT_CPU, (cpu, cpu))
        resource.setrlimit(resource.RLIMIT_AS, (mem_bytes, mem_bytes))
        resource.setrlimit(resource.RLIMIT_DATA, (mem_bytes, mem_bytes))
        resource.setrlimit(resource.RLIMIT_FSIZE, (1024 * 1024, 1024 * 1024))
        resource.setrlimit(resource.RLIMIT_NOFILE, (32, 32))
        try:
            resource.setrlimit(resource.RLIMIT_NPROC, (32, 32))
        except Exception:
            pass
    except Exception:
        pass


def _process_env(tmp: str) -> Dict[str, str]:
    return {
        "PYTHONIOENCODING": "utf-8",
        "PYTHONDONTWRITEBYTECODE": "1",
        "PYTHONUNBUFFERED": "1",
        "PYTHONNOUSERSITE": "1",
        "HOME": tmp,
        "TMPDIR": tmp,
    }


def _process_command(path: str) -> List[str]:
    cmd = [sys.executable]
    if SANDBOX_RUNNER:
        cmd.append("-I")
    cmd.append(path)
    return cmd


def _container_command(tmp: str, solution_name: str = "solution.py", stdin_name: str = "stdin.txt") -> List[str] | None:
    runtime = (RUNNER_CONTAINER_RUNTIME or "docker").strip()
    if not runtime or shutil.which(runtime) is None:
        return None
    workspace = "/workspace"
    stdin_path = f"{workspace}/{stdin_name}"
    solution_path = f"{workspace}/{solution_name}"
    cmd: List[str] = [runtime, "run", "--rm"]
    if RUNNER_CONTAINER_NETWORK:
        cmd.extend(["--network", RUNNER_CONTAINER_NETWORK])
    cmd.extend(["--read-only"])
    if RUNNER_CONTAINER_TMPFS_MB > 0:
        cmd.extend(["--tmpfs", f"/tmp:rw,noexec,nosuid,size={max(8, RUNNER_CONTAINER_TMPFS_MB)}m"])
    if RUNNER_CONTAINER_PIDS_LIMIT > 0:
        cmd.extend(["--pids-limit", str(RUNNER_CONTAINER_PIDS_LIMIT)])
    if RUNNER_CONTAINER_NO_NEW_PRIVS:
        cmd.extend(["--security-opt", "no-new-privileges"])
    cmd.extend(["--cap-drop", "ALL"])
    cmd.extend(["--cpus", str(max(1, RUNNER_CPU_SECONDS)), "--memory", f"{max(64, RUNNER_MEMORY_MB)}m"])
    cmd.extend(["-v", f"{tmp}:{workspace}:ro", "-w", workspace, RUNNER_CONTAINER_IMAGE])
    shell_cmd = f"python -I {shlex.quote(solution_path)} < {shlex.quote(stdin_path)}"
    cmd.extend(["sh", "-lc", shell_cmd])
    return cmd


def _run_subprocess(cmd: List[str], *, stdin_text: str | None, cwd: str, env: Dict[str, str], timeout: int, sandboxed: bool, mode: str) -> Dict[str, Any]:
    try:
        proc = subprocess.run(
            cmd,
            input=stdin_text,
            text=True,
            capture_output=True,
            timeout=max(1, timeout),
            cwd=cwd,
            env=env,
            preexec_fn=_sandbox_preexec if (mode == "process" and hasattr(os, "setsid")) else None,
        )
        return {
            "returncode": proc.returncode,
            "stdout": _truncate_stream(proc.stdout or "", RUNNER_STDOUT_LIMIT),
            "stderr": _truncate_stream(proc.stderr or "", RUNNER_STDERR_LIMIT),
            "timedOut": False,
            "signal": abs(proc.returncode) if isinstance(proc.returncode, int) and proc.returncode < 0 else None,
            "sandboxed": sandboxed,
            "sandboxMode": mode,
        }
    except subprocess.TimeoutExpired as ex:
        return {
            "returncode": None,
            "stdout": _truncate_stream(ex.stdout or "", RUNNER_STDOUT_LIMIT),
            "stderr": _truncate_stream(ex.stderr or "", RUNNER_STDERR_LIMIT),
            "timedOut": True,
            "signal": signal.SIGKILL,
            "sandboxed": sandboxed,
            "sandboxMode": mode,
        }


def run_python_solution(source_code: str, stdin_text: str) -> Dict[str, Any]:
    """Run a Python script in a temp directory and return stdout/stderr/rc."""
    sandbox_mode = (SANDBOX_RUNNER_MODE or "process").strip().lower() if SANDBOX_RUNNER else "process"
    with tempfile.TemporaryDirectory(prefix="tf-ai-") as tmp:
        path = os.path.join(tmp, "solution.py")
        stdin_path = os.path.join(tmp, "stdin.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(source_code)
        with open(stdin_path, "w", encoding="utf-8") as f:
            f.write(stdin_text or "")
        env = _process_env(tmp)
        if SANDBOX_RUNNER and sandbox_mode == "container":
            container_cmd = _container_command(tmp)
            if container_cmd:
                return _run_subprocess(
                    container_cmd,
                    stdin_text=None,
                    cwd=tmp,
                    env={"PATH": os.environ.get("PATH", "")},
                    timeout=max(1, RUNNER_TIMEOUT_SECONDS),
                    sandboxed=True,
                    mode="container",
                )
        return _run_subprocess(
            _process_command(path),
            stdin_text=stdin_text,
            cwd=tmp,
            env=env,
            timeout=max(1, RUNNER_TIMEOUT_SECONDS),
            sandboxed=SANDBOX_RUNNER,
            mode="process",
        )


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
            ok = runtime.get("returncode") == 0 and not runtime.get("timedOut") and actual == expected
            if ok:
                passed += 1
            results.append({
                "index": idx,
                "passed": ok,
                "returncode": runtime.get("returncode"),
                "actual": actual,
                "expected": expected,
                "stderr": truncate_text(runtime.get("stderr"), 180),
                "timedOut": bool(runtime.get("timedOut")),
                "signal": runtime.get("signal"),
                "sandboxed": bool(runtime.get("sandboxed")),
                "sandboxMode": runtime.get("sandboxMode"),
            })
        except Exception as ex:
            results.append({"index": idx, "passed": False, "error": str(ex)})
    return {"passed": passed, "total": len(results), "results": results}
