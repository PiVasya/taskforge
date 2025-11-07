from fastapi import FastAPI
from pydantic import BaseModel
import subprocess, tempfile, os, resource, sys, textwrap

app = FastAPI()

CPU_TIME_SEC = 3
MEM_BYTES = 256 * 1024 * 1024
MAX_OUT_LEN = 1_000_000

# ======== DTOs ========

class RunReq(BaseModel):
    code: str
    input: str | None = None

class TestCase(BaseModel):
    input: str | None = None
    expectedOutput: str | None = None
    name: str | None = None

class TestsReq(BaseModel):
    code: str
    tests: list[TestCase]
    fast: bool | None = False   # ← смок-режим

# ======== лимиты ========

def _limits():
    try:
        resource.setrlimit(resource.RLIMIT_CPU, (CPU_TIME_SEC, CPU_TIME_SEC))
        resource.setrlimit(resource.RLIMIT_AS, (MEM_BYTES, MEM_BYTES))
    except Exception:
        # На Windows просто пропускаем
        pass

# ======== утилиты ========

def _normalize(s: str) -> str:
    """Сравнение вывода: CRLF -> LF и убираем хвостовые переводы."""
    return (s or "").replace("\r\n", "\n").strip("\n")

def _truncate(s: str | None) -> str:
    if not s:
        return ""
    return s[:MAX_OUT_LEN]

def run_py(pyfile: str, input_txt: str | None, cwd: str, timeout_sec: int):
    p = subprocess.Popen(
        [sys.executable, pyfile],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        cwd=cwd, text=True, preexec_fn=_limits
    )
    try:
        out, err = p.communicate(input_txt or "", timeout=timeout_sec)
        return p.returncode, _truncate(out), _truncate(err)
    except subprocess.TimeoutExpired:
        p.kill()
        return 124, "", "Time limit exceeded"
    except Exception as ex:
        p.kill()
        return 1, "", str(ex)

# ======== endpoints ========

@app.post("/run")
def run(req: RunReq):
    if not req.code or not req.code.strip():
        return {"stdout": "", "stderr": "", "exitCode": 1, "error": "Code is empty."}

    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code)

        rc, out, err = run_py(path, req.input, d, 5)
        return {
            "stdout": out,
            "stderr": err,
            "exitCode": rc,
            "error": "" if rc == 0 else (err or "Runtime error")
        }

@app.post("/tests")
def run_tests(req: TestsReq):
    if not req.code or not req.code.strip():
        return {"results": [], "compileError": "Code is empty."}

    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "main.py")
        with open(path, "w", encoding="utf-8") as f:
            f.write(req.code)

        results = []
        first_fail = None

        for idx, t in enumerate(req.tests or []):
            rc, out, err = run_py(path, t.input or "", d, 5)

            exp = _normalize(t.expectedOutput or "")
            act = _normalize(out)

            passed = (rc == 0) and (act == exp)
            actual_for_client = out if out else (err or "")

            item = {
                "name": t.name or f"Test #{idx+1}",
                "input": t.input or "",
                "expectedOutput": t.expectedOutput or "",
                "actualOutput": actual_for_client,
                "passed": passed,
                "exitCode": rc,
                "stderr": err or ""
            }
            results.append(item)

            if not passed and first_fail is None:
                first_fail = item
                if req.fast:
                    break

        return {
            "results": results,
            "firstFail": first_fail,
            "compileError": ""  # для единообразия с C# раннером
        }

# Для обратной совместимости со старым маршрутом
@app.post("/run-tests")
def run_tests_alias(req: TestsReq):
    return run_tests(req)
