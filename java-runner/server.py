# java-runner/server.py
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

# компиляция и запуск Java-кода

def _compile(source: str, workdir: str):
    # компилируем Main.java в каталоге workdir
    c = subprocess.run(["javac", source], stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE, text=True, cwd=workdir)
    return c.returncode, c.stderr

# запуск java-программы

def _run(workdir: str, input_txt: str, timeout: int):
    p = subprocess.Popen(["java", "-cp", workdir, "Main"],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                         cwd=workdir, text=True, preexec_fn=_limits)
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
    with tempfile.TemporaryDirectory() as d:
        src = os.path.join(d, "Main.java")
        open(src, "w", encoding="utf-8").write(req.code)
        rc, compile_err = _compile(src, d)
        if rc != 0:
            return {"stdout": "", "stderr": f"Compilation error:\n{compile_err}", "exitCode": 2}
        rc, out, err = _run(d, req.input or "", 5)
        return {"stdout": out, "stderr": err, "exitCode": rc}

@app.post("/run/tests")
def run_tests(req: TestsReq):
    results = []
    with tempfile.TemporaryDirectory() as d:
        src = os.path.join(d, "Main.java")
        open(src, "w", encoding="utf-8").write(req.code)
        rc, compile_err = _compile(src, d)
        if rc != 0:
            # если не скомпилировалось — все тесты провалены
            return {"results": [{"input": t.get("input"),
                                  "expectedOutput": t.get("expectedOutput"),
                                  "actualOutput": "",
                                  "passed": False} for t in req.tests]}
        for t in req.tests:
            given = t.get("input") or ""
            expected = t.get("expectedOutput") or ""
            rc_run, out, err = _run(d, given, 5)
            passed = (out.rstrip("\r\n") == expected.rstrip("\r\n")) and rc_run == 0
            results.append({
                "input": given,
                "expectedOutput": expected,
                "actualOutput": out,
                "passed": passed
            })
    return {"results": results}

# старый URL
@app.post("/run-tests")
def run_tests_alias(req: TestsReq):
    return run_tests(req)
