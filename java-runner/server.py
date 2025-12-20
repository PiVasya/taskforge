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

def _set_cpu_limit(timeout_ms: int):
    sec = max(1, int(math.ceil(timeout_ms / 1000.0)) + 1)
    resource.setrlimit(resource.RLIMIT_CPU, (sec, sec))

def _java_heap_mb(mem_mb: int) -> int:
    mem_mb = max(128, mem_mb)
    # запас под metaspace/codecache/стек/ОС
    heap = mem_mb - 128
    if heap < 64:
        heap = 64
    if heap > 512:
        heap = 512
    return heap

def _java_flags(mem_mb: int):
    heap = _java_heap_mb(mem_mb)
    # Уменьшаем code cache, иначе JVM падает на маленькой памяти
    return [
        "-Xms16m",
        f"-Xmx{heap}m",
        "-XX:+UseSerialGC",
        "-XX:ReservedCodeCacheSize=16m",
        "-XX:InitialCodeCacheSize=8m",
        "-XX:MaxMetaspaceSize=64m",
        "-XX:CompressedClassSpaceSize=32m",
        "-XX:+ExitOnOutOfMemoryError",
    ]

@app.post("/run")
def run(req: RunReq):
    tl = req.timeLimitMs or 12000
    ml = req.memoryLimitMb or 768

    with tempfile.TemporaryDirectory() as d:
        src = os.path.join(d, "Main.java")
        with open(src, "w", encoding="utf-8") as f:
            f.write(req.code)

        def preexec():
            _set_cpu_limit(tl)
            # ВАЖНО: НЕ ставим RLIMIT_AS — иначе Java может падать на резервах

        # compile
        c = subprocess.run(
            ["javac", "Main.java"],
            cwd=d,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=max(2, int(math.ceil(tl / 1000.0)) + 2),
            preexec_fn=preexec
        )

        if c.returncode != 0:
            out = (c.stdout or "").replace("\r\n", "\n")[:MAX_OUT_LEN]
            err = (c.stderr or "").replace("\r\n", "\n")[:MAX_OUT_LEN]
            msg = (out + err).strip() or "Compilation error"
            return {"stdout": "", "stderr": "", "compileStderr": msg + "\n", "exitCode": 2}

        # run
        p = subprocess.Popen(
            ["java", *_java_flags(ml), "Main"],
            cwd=d,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            preexec_fn=preexec
        )

        try:
            out, err = p.communicate(req.input or "", timeout=max(2, int(math.ceil(tl / 1000.0)) + 2))
            out = out.replace("\r\n", "\n")[:MAX_OUT_LEN]
            err = err.replace("\r\n", "\n")[:MAX_OUT_LEN]
            return {"stdout": out, "stderr": err, "compileStderr": None, "exitCode": p.returncode}
        except subprocess.TimeoutExpired:
            p.kill()
            return {"stdout": "", "stderr": "Time limit exceeded", "compileStderr": None, "exitCode": 124}
