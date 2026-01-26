import base64
import os
import shutil
import subprocess
import tempfile
import time
import uuid
import logging
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field


logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO"),
    format="%(asctime)s [%(levelname)s] [python-image-runner] %(message)s",
)
logger = logging.getLogger("python-image-runner")

app = FastAPI(title="taskforge image python runner")


def _runner_env(run_id: str) -> dict:
    """Build env for subprocess.

    Important: we run user code from a temp working directory (cwd=tempdir).
    In that case `python -m app.execute` would NOT find /app/app/* unless
    we explicitly put /app into PYTHONPATH.
    """
    cur = os.environ.get("PYTHONPATH", "")
    parts = [p for p in cur.split(os.pathsep) if p]
    if "/app" not in parts:
        parts.insert(0, "/app")

    return {
        **os.environ,
        "PYTHONIOENCODING": "utf-8",
        "PYTHONUNBUFFERED": "1",
        "PYTHONPATH": os.pathsep.join(parts),
        "TF_RUN_ID": run_id,
    }


def _dump_tempdir(run_id: str, tdir: Path, title: str) -> None:
    try:
        items = []
        for it in sorted(tdir.iterdir(), key=lambda x: (not x.is_dir(), x.name.lower())):
            if it.is_dir():
                items.append(f"[DIR] {it.name}")
            else:
                try:
                    items.append(f"[FILE] {it.name} size={it.stat().st_size}")
                except Exception:
                    items.append(f"[FILE] {it.name} size=?")
        logger.info("run_id=%s %s tempdir=%s items=%s", run_id, title, tdir, "; ".join(items) if items else "<empty>")
    except Exception as e:
        logger.warning("run_id=%s %s tempdir dump failed: %s", run_id, title, e)


class RenderRequest(BaseModel):
    # User python code.
    code: str = Field(min_length=1)
    # Hard timeout in seconds.
    timeoutSeconds: int = Field(default=5, ge=1, le=30)


@app.get("/health")
def health():
    return {"ok": True}


@app.post("/render", response_class=FileResponse)
def render(req: RenderRequest):
    """Executes user python code and returns a PNG.

    Contract (v0): user code may either:
      1) explicitly create out.png in the current working directory (preferred), OR
      2) draw using turtle; we'll capture the canvas automatically.

    The runner is intentionally *stateless* and returns the generated image bytes.
    """

    run_id = str(uuid.uuid4())
    t0 = time.time()
    logger.info("run_id=%s start /render timeoutSeconds=%s codeLen=%s", run_id, req.timeoutSeconds, len(req.code))

    if not shutil.which("xvfb-run"):
        raise HTTPException(500, "xvfb-run not found")

    with tempfile.TemporaryDirectory(prefix="tf-img-py-") as td:
        tdir = Path(td)
        user_path = tdir / "user.py"
        out_png = tdir / "out.png"
        user_path.write_text(req.code, encoding="utf-8")
        logger.info("run_id=%s wrote user.py size=%s", run_id, user_path.stat().st_size)

        # execute.py will try:
        #  - run user code
        #  - if out.png exists -> ok
        #  - else, try capture turtle canvas -> create out.png
        cmd = [
            "xvfb-run",
            "-a",
            "python",
            "-m",
            "app.execute",
            str(user_path),
            str(out_png),
        ]

        logger.info(
            "run_id=%s exec cmd=%s cwd=%s PYTHONPATH=%s",
            run_id,
            " ".join(cmd),
            str(tdir),
            _runner_env(run_id).get("PYTHONPATH"),
        )

        try:
            p = subprocess.run(
                cmd,
                cwd=str(tdir),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=req.timeoutSeconds,
                check=False,
                env=_runner_env(run_id),
            )
        except subprocess.TimeoutExpired:
            logger.warning("run_id=%s timeout after %ss", run_id, req.timeoutSeconds)
            raise HTTPException(408, "Execution timed out")

        logger.info("run_id=%s runner exitCode=%s stdoutLen=%s stderrLen=%s", run_id, p.returncode, len(p.stdout), len(p.stderr))

        if p.returncode != 0:
            err = p.stderr.decode("utf-8", errors="replace")
            out = p.stdout.decode("utf-8", errors="replace")
            _dump_tempdir(run_id, tdir, "FAIL")
            logger.warning("run_id=%s execution failed", run_id)
            raise HTTPException(
                400,
                {
                    "message": "Execution failed",
                    "runId": run_id,
                    "stdout": out[-4000:],
                    "stderr": err[-4000:],
                },
            )

        if not out_png.exists() or out_png.stat().st_size == 0:
            err = p.stderr.decode("utf-8", errors="replace")
            out = p.stdout.decode("utf-8", errors="replace")
            _dump_tempdir(run_id, tdir, "NO_PNG")
            logger.warning("run_id=%s no image produced", run_id)
            raise HTTPException(
                400,
                {
                    "message": "No image produced. Create 'out.png' in the current working directory or draw with turtle.",
                    "runId": run_id,
                    "stdout": out[-2000:],
                    "stderr": err[-2000:],
                },
            )

        logger.info("run_id=%s ok imageBytes=%s elapsedMs=%s", run_id, out_png.stat().st_size, int((time.time()-t0)*1000))
        return FileResponse(path=str(out_png), media_type="image/png", filename="out.png")


class RenderBase64Response(BaseModel):
    pngBase64: str


class RenderDebugResponse(BaseModel):
    pngBase64: str
    stdout: str
    stderr: str


@app.post("/render/base64", response_model=RenderBase64Response)
def render_base64(req: RenderRequest):
    # Reuse binary render, but encode.
    run_id = str(uuid.uuid4())
    with tempfile.TemporaryDirectory(prefix="tf-img-py-") as td:
        tdir = Path(td)
        user_path = tdir / "user.py"
        out_png = tdir / "out.png"
        user_path.write_text(req.code, encoding="utf-8")
        cmd = ["xvfb-run", "-a", "python", "-m", "app.execute", str(user_path), str(out_png)]
        logger.info(
            "run_id=%s start /render/base64 timeoutSeconds=%s codeLen=%s cmd=%s cwd=%s PYTHONPATH=%s",
            run_id,
            req.timeoutSeconds,
            len(req.code),
            " ".join(cmd),
            str(tdir),
            _runner_env(run_id).get("PYTHONPATH"),
        )
        try:
            p = subprocess.run(
                cmd,
                cwd=str(tdir),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=req.timeoutSeconds,
                check=False,
                env=_runner_env(run_id),
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(408, "Execution timed out")
        if p.returncode != 0 or not out_png.exists() or out_png.stat().st_size == 0:
            err = p.stderr.decode("utf-8", errors="replace")
            out = p.stdout.decode("utf-8", errors="replace")
            _dump_tempdir(run_id, tdir, "FAIL")
            raise HTTPException(400, {"message": "Execution failed", "runId": run_id, "stdout": out[-2000:], "stderr": err[-2000:]})
        b = out_png.read_bytes()
        return RenderBase64Response(pngBase64=base64.b64encode(b).decode("ascii"))


@app.post("/render/debug", response_model=RenderDebugResponse)
def render_debug(req: RenderRequest):
    """Same as /render/base64, but always returns stdout/stderr for troubleshooting."""
    run_id = str(uuid.uuid4())
    with tempfile.TemporaryDirectory(prefix="tf-img-py-") as td:
        tdir = Path(td)
        user_path = tdir / "user.py"
        out_png = tdir / "out.png"
        user_path.write_text(req.code, encoding="utf-8")
        cmd = ["xvfb-run", "-a", "python", "-m", "app.execute", str(user_path), str(out_png)]
        logger.info(
            "run_id=%s start /render/debug timeoutSeconds=%s codeLen=%s cmd=%s cwd=%s PYTHONPATH=%s",
            run_id,
            req.timeoutSeconds,
            len(req.code),
            " ".join(cmd),
            str(tdir),
            _runner_env(run_id).get("PYTHONPATH"),
        )
        try:
            p = subprocess.run(
                cmd,
                cwd=str(tdir),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=req.timeoutSeconds,
                check=False,
                env=_runner_env(run_id),
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(408, "Execution timed out")

        out = p.stdout.decode("utf-8", errors="replace")
        err = p.stderr.decode("utf-8", errors="replace")

        if p.returncode != 0 or not out_png.exists() or out_png.stat().st_size == 0:
            _dump_tempdir(run_id, tdir, "FAIL")
            raise HTTPException(400, {"message": "Execution failed", "runId": run_id, "stdout": out[-4000:], "stderr": err[-4000:]})

        b = out_png.read_bytes()
        return RenderDebugResponse(
            pngBase64=base64.b64encode(b).decode("ascii"),
            stdout=(out[-10000:] if out else ""),
            stderr=(err[-10000:] if err else ""),
        )