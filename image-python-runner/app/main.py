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
from fastapi.responses import Response
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
    # NOTE: turtle drawing can be SLOWNESS in headless; keep the default a bit higher.
    # Can be overridden via request body or TF_PY_TIMEOUT_DEFAULT env var.
    timeoutSeconds: int = Field(default=int(os.environ.get("TF_PY_TIMEOUT_DEFAULT", "15")), ge=1, le=120)


def _tail(s: str, max_chars: int) -> str:
    if not s:
        return ""
    return s if len(s) <= max_chars else ("…" + s[-max_chars:])


def _run_subprocess(run_id: str, cmd: list[str], cwd: str, env: dict, timeout_seconds: int) -> tuple[int | None, str, str, bool]:
    """Run child process and always capture stdout/stderr.

    Why not subprocess.run(capture_output=True)?
    - We want partial stdout/stderr on timeouts.
    - We want to log tails to container logs in a consistent way.

    Returns: (exit_code, stdout, stderr, timed_out)
    """

    p = subprocess.Popen(
        cmd,
        cwd=cwd,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )

    try:
        out, err = p.communicate(timeout=timeout_seconds)
        return p.returncode, out or "", err or "", False
    except subprocess.TimeoutExpired:
        logger.warning("run_id=%s timeout after %ss (terminating)", run_id, timeout_seconds)
        # Try graceful terminate, then kill.
        try:
            p.terminate()
        except Exception:
            pass
        try:
            out, err = p.communicate(timeout=1)
        except Exception:
            out, err = "", ""
        try:
            p.kill()
        except Exception:
            pass
        try:
            out2, err2 = p.communicate(timeout=1)
            out = (out or "") + (out2 or "")
            err = (err or "") + (err2 or "")
        except Exception:
            pass
        return None, out or "", err or "", True


def _log_child_output(run_id: str, out: str, err: str, *, success: bool) -> None:
    """Log child stdout/stderr into *container logs*.

    Требование проекта: логи должны быть ВСЕГДА, без флагов в запросе.
    Поэтому печатаем хвост stdout/stderr и при успехе тоже (ограничиваем длину).
    """

    max_chars = int(os.environ.get("TF_CHILD_LOG_TAIL_CHARS", "4000"))
    o = _tail(out.strip(), max_chars)
    e = _tail(err.strip(), max_chars)

    if o:
        (logger.info if success else logger.warning)("run_id=%s child stdout (tail):\n%s", run_id, o)
    if e:
        (logger.info if success else logger.warning)("run_id=%s child stderr (tail):\n%s", run_id, e)


@app.get("/health")
def health():
    return {"ok": True}


@app.post("/render")
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

        env = _runner_env(run_id)
        exit_code, out, err, timed_out = _run_subprocess(run_id, cmd, str(tdir), env, req.timeoutSeconds)
        logger.info(
            "run_id=%s runner done exitCode=%s timedOut=%s stdoutLen=%s stderrLen=%s",
            run_id,
            exit_code,
            timed_out,
            len(out),
            len(err),
        )

        if timed_out:
            _dump_tempdir(run_id, tdir, "TIMEOUT")
            _log_child_output(run_id, out, err, success=False)
            raise HTTPException(408, {"message": "Execution timed out", "runId": run_id, "stdout": _tail(out, 4000), "stderr": _tail(err, 4000)})

        if exit_code != 0:
            _dump_tempdir(run_id, tdir, "FAIL")
            _log_child_output(run_id, out, err, success=False)
            raise HTTPException(400, {"message": "Execution failed", "runId": run_id, "stdout": _tail(out, 4000), "stderr": _tail(err, 4000)})

        if not out_png.exists() or out_png.stat().st_size == 0:
            _dump_tempdir(run_id, tdir, "NO_PNG")
            _log_child_output(run_id, out, err, success=False)
            raise HTTPException(
                400,
                {
                    "message": "No image produced. Create 'out.png' in the current working directory or draw with turtle.",
                    "runId": run_id,
                    "stdout": _tail(out, 2000),
                    "stderr": _tail(err, 2000),
                },
            )

        # ALWAYS log child output into container logs.
        _log_child_output(run_id, out, err, success=True)

        # IMPORTANT:
        # Do NOT return FileResponse from a TemporaryDirectory:
        # the temp folder is deleted right after we return from this function,
        # but FileResponse streams the file later -> empty/failed response.
        png_bytes = out_png.read_bytes()
        logger.info("run_id=%s ok imageBytes=%s elapsedMs=%s", run_id, len(png_bytes), int((time.time()-t0)*1000))
        return Response(content=png_bytes, media_type="image/png")


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
        env = _runner_env(run_id)
        exit_code, out, err, timed_out = _run_subprocess(run_id, cmd, str(tdir), env, req.timeoutSeconds)
        logger.info(
            "run_id=%s runner done exitCode=%s timedOut=%s stdoutLen=%s stderrLen=%s",
            run_id,
            exit_code,
            timed_out,
            len(out),
            len(err),
        )

        if timed_out:
            _dump_tempdir(run_id, tdir, "TIMEOUT")
            _log_child_output(run_id, out, err, success=False)
            raise HTTPException(408, {"message": "Execution timed out", "runId": run_id, "stdout": _tail(out, 4000), "stderr": _tail(err, 4000)})

        if exit_code != 0 or not out_png.exists() or out_png.stat().st_size == 0:
            _dump_tempdir(run_id, tdir, "FAIL")
            _log_child_output(run_id, out, err, success=False)
            raise HTTPException(400, {"message": "Execution failed", "runId": run_id, "stdout": _tail(out, 4000), "stderr": _tail(err, 4000)})
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
        env = _runner_env(run_id)
        exit_code, out, err, timed_out = _run_subprocess(run_id, cmd, str(tdir), env, req.timeoutSeconds)
        logger.info(
            "run_id=%s runner done exitCode=%s timedOut=%s stdoutLen=%s stderrLen=%s",
            run_id,
            exit_code,
            timed_out,
            len(out),
            len(err),
        )

        if timed_out:
            _dump_tempdir(run_id, tdir, "TIMEOUT")
            _log_child_output(run_id, out, err, success=False)
            raise HTTPException(408, {"message": "Execution timed out", "runId": run_id, "stdout": _tail(out, 4000), "stderr": _tail(err, 4000)})

        if exit_code != 0 or not out_png.exists() or out_png.stat().st_size == 0:
            _dump_tempdir(run_id, tdir, "FAIL")
            _log_child_output(run_id, out, err, success=False)
            raise HTTPException(400, {"message": "Execution failed", "runId": run_id, "stdout": _tail(out, 4000), "stderr": _tail(err, 4000)})

        b = out_png.read_bytes()
        return RenderDebugResponse(
            pngBase64=base64.b64encode(b).decode("ascii"),
            stdout=(out[-10000:] if out else ""),
            stderr=(err[-10000:] if err else ""),
        )