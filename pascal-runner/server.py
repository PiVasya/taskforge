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

def _limits(timeout_ms: int, mem_mb: int):
    sec = max(1, int(math.ceil(timeout_ms / 1000.0)) + 1)
    resource.setrlimit(resource.RLIMIT_CPU, (sec, sec))

    # Для паскаля RLIMIT_AS можно оставить — он не резервирует как JVM/V8
    mem_bytes = max(64, mem_mb) * 1024 * 1024
    resource.setrlimit(resource.RLIMIT_AS, (mem_bytes, mem_bytes))

@app.post("/run")
def run(req: RunReq):
    tl = req.timeLimitMs or 8000
    ml = req.memoryLimitMb or 256

    with tempfile.TemporaryDirectory() as d:
        src = os.path.join(d, "main.pas")
        exe = os.path.join(d, "main")

        with open(src, "w", encoding="utf-8") as f:
            f.write(req.code)

        def preexec():
            _limits(tl, ml)

        # compile (FreePascal)
        c = subprocess.run(
            ["fpc", "main.pas", "-O2", "-vw", f"-o{exe}"],
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
            details = (out + err).strip()
            if not details:
                details = "Compiler produced no output"
            return {"stdout": "", "stderr": "Compilation error:\n" + details + "\n", "exitCode": 2}

        # run
        p = subprocess.Popen(
            [exe],
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
            return {"stdout": out, "stderr": err, "exitCode": p.returncode}
        except subprocess.TimeoutExpired:
            p.kill()
            return {"stdout": "", "stderr": "Time limit exceeded", "exitCode": 124}
