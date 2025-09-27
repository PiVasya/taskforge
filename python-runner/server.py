from fastapi import FastAPI
from pydantic import BaseModel
import subprocess, tempfile, os, resource

app = FastAPI()

CPU_TIME_SEC = 3
MEM_BYTES = 256 * 1024 * 1024

class RunReq(BaseModel):
    code: str
    input: str | None = None

class TestsReq(BaseModel):
    code: str
    tests: list[dict]

def _limits():
    resource.setrlimit(resource.RLIMIT_CPU, (CPU_TIME_SEC, CPU_TIME_SEC))
    resource.setrlimit(resource.RLIMIT_AS, (MEM_BYTES, MEM_BYTES))

def run_py(pyfile, input_txt, cwd, timeout):
    p = subprocess.Popen(
        ["python", pyfile],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        cwd=cwd, text=True, preexec_fn=_limits
    )
    try:
        out, err = p.communicate(input_txt or "", timeout=timeout)
        return p.returncode, out, err
    except subprocess.TimeoutExpired:
        p.kill()
        return 124, "", "Time limit exceeded"

@app.post("/run")
def run(req: RunReq):
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.py")
        open(path, "w", encoding="utf-8").write(req.code)
        rc, out, err = run_py(path, req.input, d, 5)
        return {"stdout": out, "stderr": err, "exitCode": rc}

@app.post("/run/tests")
def run_tests(req: TestsReq):
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.py")
        open(path, "w", encoding="utf-8").write(req.code)
        results = []
        for t in req.tests:
            rc, out, err = run_py(path, t.get("input") or "", d, 5)
            results.append({
                "input": t.get("input"),
                "expectedOutput": t.get("expectedOutput"),
                "actualOutput": out,
                "passed": (out == t.get("expectedOutput"))
            })
        return results
