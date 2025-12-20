from fastapi import FastAPI
from pydantic import BaseModel
import subprocess
import tempfile
import os
import resource
import math

app = FastAPI()

MAX_OUT_LEN = 1_000_000

class RunReq(BaseModel):
    code: str
    input: str | None = None
    timeLimitMs: int | None = None
    memoryLimitMb: int | None = None

class TestCase(BaseModel):
    input: str | None = None
    expectedOutput: str | None = None

class TestsReq(BaseModel):
    code: str
    tests: list[TestCase]
    timeLimitMs: int | None = None
    memoryLimitMb: int | None = None

def _set_cpu_limit(timeout_ms: int):
    # RLIMIT_CPU в секундах, округлим вверх + небольшой запас
    sec = max(1, int(math.ceil(timeout_ms / 1000.0)) + 1)
    resource.setrlimit(resource.RLIMIT_CPU, (sec, sec))

def _node_heap_mb(mem_mb: int) -> int:
    # Оставим запас под V8/стек/код/буферы
    # Например: при mem=256 -> heap ~128
    mem_mb = max(64, mem_mb)
    heap = mem_mb - 96
    if heap < 64:
        heap = 64
    # верх можно ограничить, чтобы не раздувалось
    if heap > 512:
        heap = 512
    return heap

def run_js(jsfile: str, input_txt: str, cwd: str, timeout_ms: int, mem_mb: int):
    heap = _node_heap_mb(mem_mb)

    def preexec():
        _set_cpu_limit(timeout_ms)
        # ВАЖНО: НЕ ставим RLIMIT_AS — иначе V8 часто падает на CodeRange

    p = subprocess.Popen(
        ["node", f"--max-old-space-size={heap}", jsfile],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
        text=True,
        preexec_fn=preexec
    )

    try:
        out, err = p.communicate(input_txt or "", timeout=max(1, int(math.ceil(timeout_ms / 1000.0)) + 2))
        out = out.replace("\r\n", "\n")[:MAX_OUT_LEN]
        err = err.replace("\r\n", "\n")[:MAX_OUT_LEN]
        return p.returncode, out, err
    except subprocess.TimeoutExpired:
        p.kill()
        return 124, "", "Time limit exceeded"

@app.post("/run")
def run(req: RunReq):
    tl = req.timeLimitMs or 8000
    ml = req.memoryLimitMb or 512

    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.js")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code)

        rc, out, err = run_js(path, req.input or "", d, tl, ml)
        return {"stdout": out, "stderr": err, "exitCode": rc}

@app.post("/run/tests")
def run_tests(req: TestsReq):
    tl = req.timeLimitMs or 8000
    ml = req.memoryLimitMb or 512

    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.js")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code)

        results = []
        for t in req.tests:
            given = t.input or ""
            expected = t.expectedOutput or ""
            rc, out, err = run_js(path, given, d, tl, ml)
            passed = (rc == 0) and (out.rstrip("\r\n") == expected.rstrip("\r\n"))
            results.append({
                "input": given,
                "expectedOutput": expected,
                "actualOutput": out,
                "passed": passed,
                "stderr": err
            })

        return {"results": results}

# Backward-compatible alias (если где-то дергаешь так)
@app.post("/run-tests")
def run_tests_alias(req: TestsReq):
    return run_tests(req)
