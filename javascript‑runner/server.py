from fastapi import FastAPI
from pydantic import BaseModel
import subprocess
import tempfile
import os
import resource

app = FastAPI()

# Resource limits: CPU time (seconds) and memory (bytes)
CPU_TIME_SEC = 3
MEM_BYTES = 256 * 1024 * 1024
MAX_OUT_LEN = 1_000_000

class RunReq(BaseModel):
    code: str
    input: str | None = None

class TestCase(BaseModel):
    input: str | None = None
    expectedOutput: str | None = None

class TestsReq(BaseModel):
    code: str
    tests: list[TestCase]

def _limits():
    """Apply CPU and memory limits to the child process."""
    resource.setrlimit(resource.RLIMIT_CPU, (CPU_TIME_SEC, CPU_TIME_SEC))
    resource.setrlimit(resource.RLIMIT_AS, (MEM_BYTES, MEM_BYTES))

def run_js(jsfile: str, input_txt: str, cwd: str, timeout: int):
    """Compile and run a JavaScript file using Node.js with timeouts and limits."""
    p = subprocess.Popen(
        ["node", jsfile],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
        text=True,
        preexec_fn=_limits
    )
    try:
        out, err = p.communicate(input_txt or "", timeout=timeout)
        out = out.replace("\r\n", "\n")[:MAX_OUT_LEN]
        err = err.replace("\r\n", "\n")[:MAX_OUT_LEN]
        return p.returncode, out, err
    except subprocess.TimeoutExpired:
        p.kill()
        return 124, "", "Time limit exceeded"

@app.post("/run")
def run(req: RunReq):
    """Compile and run a single JavaScript program."""
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.js")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code)
        rc, out, err = run_js(path, req.input or "", d, timeout=5)
        return {"stdout": out, "stderr": err, "exitCode": rc}

@app.post("/run/tests")
def run_tests(req: TestsReq):
    """Run multiple tests against a single JavaScript program."""
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.js")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code)
        results = []
        for t in req.tests:
            given = t.input or ""
            expected = t.expectedOutput or ""
            rc, out, err = run_js(path, given, d, timeout=5)
            passed = (out.rstrip("\r\n") == expected.rstrip("\r\n")) and rc == 0
            results.append({
                "input": given,
                "expectedOutput": expected,
                "actualOutput": out,
                "passed": passed,
                "stderr": err
            })
        return {"results": results}

# Backward‑compatible alias
@app.post("/run-tests")
def run_tests_alias(req: TestsReq):
    return run_tests(req)
