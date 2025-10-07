# app.py
from fastapi import FastAPI
from pydantic import BaseModel
import subprocess, tempfile, os, resource, signal

app = FastAPI()

DEFAULT_CPU_LIMIT_MS = 3000        # мс
DEFAULT_MEM_MB = 256               # мегабайт
MAX_OUT_LEN = 1_000_000            # защита от заливания логов

# --------- запросы ----------
class RunReq(BaseModel):
    code: str
    input: str | None = None
    timeLimitMs: int | None = None
    memoryLimitMb: int | None = None

class TestsReq(BaseModel):
    code: str
    tests: list[dict]               # { input, expectedOutput, isHidden? }
    timeLimitMs: int | None = None
    memoryLimitMb: int | None = None

# --------- системные лимиты ----------
def _apply_limits(cpu_ms: int, mem_mb: int):
    cpu_sec = max(1, int(round(cpu_ms / 1000)))
    mem_bytes = max(64, mem_mb) * 1024 * 1024
    resource.setrlimit(resource.RLIMIT_CPU, (cpu_sec, cpu_sec))
    # RLIMIT_AS может быть неэффективен в контейнере, но ставим для приличия
    try:
        resource.setrlimit(resource.RLIMIT_AS, (mem_bytes, mem_bytes))
    except Exception:
        pass

def _normalize(s: str) -> str:
    return (s or "").replace("\r\n", "\n")[:MAX_OUT_LEN]

# --------- основа: компиляция + запуск ----------
def _compile_and_run(code: str, input_txt: str, time_ms: int, mem_mb: int):
    with tempfile.TemporaryDirectory() as d:
        src = os.path.join(d, "main.cpp")
        bin_path = os.path.join(d, "a.out")
        with open(src, "w", encoding="utf-8") as f:
            f.write(code)

        # компиляция
        c = subprocess.run(
            ["g++", "-std=c++17", "-O2", "-pipe", "-static-libgcc", "-static-libstdc++", src, "-o", bin_path],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True
        )
        if c.returncode != 0:
            return {
                "status": "compile_error",
                "exitCode": c.returncode,
                "stdout": "",
                "stderr": "",
                "compileStderr": _normalize(c.stderr),
            }

        # запуск бинарника с лимитами
        p = subprocess.Popen(
            [bin_path],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            cwd=d, text=True,
            preexec_fn=lambda: _apply_limits(time_ms, mem_mb),
        )
        try:
            out, err = p.communicate(input_txt or "", timeout=max(1, int(round(time_ms/1000))+1))
            result = {
                "status": "ok" if p.returncode == 0 else "runtime_error",
                "exitCode": p.returncode,
                "stdout": _normalize(out),
                "stderr": _normalize(err),
                "compileStderr": None,
            }
            return result
        except subprocess.TimeoutExpired:
            try:
                p.kill()
            except Exception:
                pass
            return {
                "status": "time_limit",
                "exitCode": 124,
                "stdout": "",
                "stderr": "Time limit exceeded",
                "compileStderr": None,
            }

# --------- endpoints ----------
@app.post("/run")
def run(req: RunReq):
    time_ms = req.timeLimitMs or DEFAULT_CPU_LIMIT_MS
    mem_mb  = req.memoryLimitMb or DEFAULT_MEM_MB
    r = _compile_and_run(req.code, req.input or "", time_ms, mem_mb)
    # подровняем контракт под фронт
    return {
        "status": r["status"],
        "exitCode": r["exitCode"],
        "stdout": r["stdout"],
        "stderr": r["stderr"],
        "compileStderr": r.get("compileStderr"),
    }

@app.post("/run/tests")
def run_tests(req: TestsReq):
    time_ms = req.timeLimitMs or DEFAULT_CPU_LIMIT_MS
    mem_mb  = req.memoryLimitMb or DEFAULT_MEM_MB

    # одна компиляция — много запусков: можно оптимизировать, но пока проще
    # (если потребуется, вынесем компиляцию отдельно и будем гонять один бинарник)
    results = []
    for t in (req.tests or []):
        given = (t.get("input") or "")
        expected = (t.get("expectedOutput") or "")

        r = _compile_and_run(req.code, given, time_ms, mem_mb)

        # сравнение только если статус "ok"
        passed = False
        if r["status"] == "ok":
            passed = r["stdout"].rstrip("\r\n") == expected.rstrip("\r\n")

        results.append({
            "input": given,
            "expectedOutput": expected,
            "actualOutput": r["stdout"],
            "passed": passed,
            "status": r["status"],
            "exitCode": r["exitCode"],
            "stderr": r["stderr"],
            "compileStderr": r.get("compileStderr"),
            "hidden": bool(t.get("isHidden", False)),
        })

        # если компиляция не удалась — дальше тесты смысла гонять нет
        if r["status"] == "compile_error":
            break

    return {"results": results}

# старый алиас, если где-то дергается
@app.post("/run-tests")
def run_tests_alias(req: TestsReq):
    return run_tests(req)
