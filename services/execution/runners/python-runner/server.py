from __future__ import annotations

import os
import resource
import signal
import subprocess
import sys
import tempfile
from typing import Any

from fastapi import FastAPI
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge-python-runner")

MAX_OUT_LEN = 1_000_000


class RunReq(BaseModel):
    code: str = ""
    input: str | None = None
    timeLimitMs: int | None = None
    memoryLimitMb: int | None = None


class TestCase(BaseModel):
    input: str | None = None
    expectedOutput: str | None = None
    isHidden: bool = False


class TestsReq(BaseModel):
    code: str = ""
    tests: list[TestCase] = Field(default_factory=list)
    timeLimitMs: int | None = None
    memoryLimitMb: int | None = None


def _time_limit_seconds(ms: int | None) -> int:
    if not ms or ms <= 0:
        return 5
    return max(1, min(30, int((ms + 999) / 1000)))


def _timeout_seconds(ms: int | None) -> float:
    if not ms or ms <= 0:
        return 7.0
    return max(1.0, min(35.0, (ms / 1000.0) + 2.0))


def _memory_bytes(mb: int | None) -> int:
    value = mb if mb and mb > 0 else 256
    value = max(64, min(1024, int(value)))
    return value * 1024 * 1024


def _limits(cpu_seconds: int, mem_bytes: int):
    def apply() -> None:
        try:
            resource.setrlimit(resource.RLIMIT_CPU, (cpu_seconds, cpu_seconds + 1))
        except Exception:
            pass
        try:
            resource.setrlimit(resource.RLIMIT_AS, (mem_bytes, mem_bytes))
        except Exception:
            pass
        try:
            os.setsid()
        except Exception:
            pass

    return apply


def _canon(s: str | None) -> str:
    if not s:
        return ""
    s = s.replace("\r\n", "\n").replace("\r", "\n")
    s = "\n".join(line.rstrip(" \t") for line in s.split("\n"))
    return s.rstrip("\n")




def _compile_check(pyfile: str, cwd: str, time_ms: int | None, mem_mb: int | None) -> dict[str, Any] | None:
    p = subprocess.Popen(
        [sys.executable, "-I", "-B", "-m", "py_compile", pyfile],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
        text=True,
        preexec_fn=_limits(_time_limit_seconds(time_ms), _memory_bytes(mem_mb)),
    )

    try:
        out, err = p.communicate(timeout=_timeout_seconds(time_ms))
    except subprocess.TimeoutExpired:
        try:
            os.killpg(os.getpgid(p.pid), signal.SIGKILL)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass
        return {"status": "compile_error", "stdout": "", "stderr": "Compilation timeout", "exitCode": 124, "compileStderr": "Compilation timeout"}

    if p.returncode == 0:
        return None

    msg = ((out or "") + (err or "")).replace("\r\n", "\n")[:MAX_OUT_LEN]
    if not msg.strip():
        msg = "Python syntax error"
    return {"status": "compile_error", "stdout": "", "stderr": "", "exitCode": int(p.returncode or 1), "compileStderr": msg}

def _run_py(pyfile: str, input_txt: str | None, cwd: str, time_ms: int | None, mem_mb: int | None) -> dict[str, Any]:
    data = input_txt or ""
    if data == "":
        data = "\n"

    p = subprocess.Popen(
        [sys.executable, "-I", "-B", pyfile],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
        text=True,
        preexec_fn=_limits(_time_limit_seconds(time_ms), _memory_bytes(mem_mb)),
    )

    try:
        out, err = p.communicate(data, timeout=_timeout_seconds(time_ms))
        out = out.replace("\r\n", "\n")[:MAX_OUT_LEN]
        err = err.replace("\r\n", "\n")[:MAX_OUT_LEN]
        status = "ok" if p.returncode == 0 else "runtime_error"
        return {"status": status, "stdout": out, "stderr": err, "exitCode": int(p.returncode or 0), "compileStderr": None}
    except subprocess.TimeoutExpired:
        try:
            os.killpg(os.getpgid(p.pid), signal.SIGKILL)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass
        return {"status": "time_limit", "stdout": "", "stderr": "Time limit exceeded", "exitCode": 124, "compileStderr": None}


@app.get("/health")
def health():
    return {"ok": True, "service": "python-runner", "runtime": "python", "python": sys.version.split()[0]}


@app.get("/ready")
def ready():
    return {"ok": True}


@app.post("/run")
def run(req: RunReq):
    with tempfile.TemporaryDirectory(prefix="taskforge-python-") as d:
        path = os.path.join(d, "main.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code or "")
        compile_error = _compile_check(path, d, req.timeLimitMs, req.memoryLimitMb)
        if compile_error is not None:
            return compile_error
        return _run_py(path, req.input, d, req.timeLimitMs, req.memoryLimitMb)


@app.post("/run/tests")
def run_tests(req: TestsReq):
    with tempfile.TemporaryDirectory(prefix="taskforge-python-tests-") as d:
        path = os.path.join(d, "main.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code or "")

        results: list[dict[str, Any]] = []
        compile_error = _compile_check(path, d, req.timeLimitMs, req.memoryLimitMb)
        if compile_error is not None:
            first = req.tests[0] if req.tests else TestCase()
            return {"results": [{
                "input": first.input or "",
                "expectedOutput": first.expectedOutput or "",
                "actualOutput": "",
                "passed": False,
                "status": "compile_error",
                "exitCode": compile_error.get("exitCode", 1),
                "stderr": compile_error.get("stderr") or "",
                "compileStderr": compile_error.get("compileStderr") or "Python syntax error",
                "hidden": bool(first.isHidden),
            }]}

        for t in req.tests:
            run_result = _run_py(path, t.input or "", d, req.timeLimitMs, req.memoryLimitMb)
            actual = run_result.get("stdout") or ""
            expected = t.expectedOutput or ""
            passed = run_result.get("exitCode") == 0 and _canon(actual) == _canon(expected)
            results.append({
                "input": t.input or "",
                "expectedOutput": expected,
                "actualOutput": actual,
                "passed": bool(passed),
                "status": run_result.get("status"),
                "exitCode": run_result.get("exitCode"),
                "stderr": run_result.get("stderr") or "",
                "compileStderr": run_result.get("compileStderr"),
                "hidden": bool(t.isHidden),
            })
        return {"results": results}


@app.post("/run-tests")
def run_tests_alias(req: TestsReq):
    return run_tests(req)
