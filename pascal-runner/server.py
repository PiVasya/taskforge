# pascal-runner/server.py
from fastapi import FastAPI
from pydantic import BaseModel
import subprocess, tempfile, os, resource

app = FastAPI()

CPU_TIME_SEC = 3
MEM_BYTES = 256 * 1024 * 1024
MAX_OUT_LEN = 1_000_000

class RunReq(BaseModel):
    code: str
    input: str | None = None

class TestsReq(BaseModel):
    code: str
    tests: list[dict]

def _limits():
    resource.setrlimit(resource.RLIMIT_CPU, (CPU_TIME_SEC, CPU_TIME_SEC))
    resource.setrlimit(resource.RLIMIT_AS, (MEM_BYTES, MEM_BYTES))

# компиляция и запуск Pascal-кода
def _compile_and_run(code: str, input_txt: str, timeout: int):
    with tempfile.TemporaryDirectory() as d:
        src = os.path.join(d, "main.pas")
        exe = os.path.join(d, "main")
        open(src, "w", encoding="utf-8").write(code)
        # компиляция
        c = subprocess.run(["fpc", "-O2", src, "-o", exe],
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if c.returncode != 0:
            return 2, "", f"Compilation error:\n{c.stderr}"
        # запуск
        p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, cwd=d, text=True, preexec_fn=_limits)
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
    rc, out, err = _compile_and_run(req.code, req.input or "", 5)
    return {"stdout": out, "stderr": err, "exitCode": rc}

@app.post("/run/tests")
def run_tests(req: TestsReq):
    results = []
    for t in req.tests:
        given = t.get("input") or ""
        expected = t.get("expectedOutput") or ""
        rc, out, err = _compile_and_run(req.code, given, 5)
        passed = (out.rstrip("\r\n") == expected.rstrip("\r\n")) and rc == 0
        results.append({
            "input": given,
            "expectedOutput": expected,
            "actualOutput": out,
            "passed": passed,
        })
    return {"results": results}

# совместимость со старым маршрутом
@app.post("/run-tests")
def run_tests_alias(req: TestsReq):
    return run_tests(req)
