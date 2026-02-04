import os
import subprocess
import tempfile
import time
import uuid
import logging
import sys
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import Response
from pydantic import BaseModel, Field

from .ui_runner import run_ui_and_capture


app = FastAPI(title="taskforge pascal image runner (GraphABC only)")

# =========================================================
# LOGGING: максимально подробно, всё в stdout контейнера
# =========================================================
_LOG = logging.getLogger("tf.pascal.image.runner")
_LOG.setLevel(logging.DEBUG)
if not _LOG.handlers:
    h = logging.StreamHandler(sys.stdout)
    h.setLevel(logging.DEBUG)
    h.setFormatter(logging.Formatter("%(asctime)s | %(levelname)s | %(message)s"))
    _LOG.addHandler(h)
    _LOG.propagate = False


class RenderRequest(BaseModel):
    # PascalABC.NET source code (GraphABC)
    source: str = Field(..., description="PascalABC.NET source code")
    # Runner total budget (compile+run)
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Verbose logs in HTTP error body")


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

TRIM_DEFAULT = os.getenv("TF_TRIM", "1").strip().lower() not in ("0", "false", "no", "off")


@app.middleware("http")
async def _log_requests(request: Request, call_next):
    rid = request.headers.get("x-request-id") or str(uuid.uuid4())
    request.state.rid = rid
    t0 = time.perf_counter()
    try:
        body = await request.body()
    except Exception:
        body = b""

    _LOG.debug(
        "RID=%s IN %s %s | client=%s | content-type=%s | body_bytes=%s",
        rid,
        request.method,
        request.url.path,
        getattr(request.client, "host", "?"),
        request.headers.get("content-type"),
        len(body),
    )
    if body:
        # Печатаем первые 2000 байт тела (чтобы сразу видеть, что прилетело)
        try:
            _LOG.debug("RID=%s BODY_HEAD=%s", rid, body[:2000].decode("utf-8", errors="replace"))
        except Exception:
            _LOG.debug("RID=%s BODY_HEAD=<decode failed>", rid)

    try:
        resp = await call_next(request)
    finally:
        dt = (time.perf_counter() - t0) * 1000.0
        _LOG.debug("RID=%s OUT %s %s | %.1fms", rid, request.method, request.url.path, dt)
    return resp


@app.get("/health")
def health():
    return {"ok": True}


def _detect_mode(src: str) -> str:
    """Image runner supports GraphABC only (DrawMan is intentionally disabled)."""
    s = (src or "").lower()
    if "drawman" in s:
        return "UNSUPPORTED_DRAWMAN"
    return "GraphABC"


def _tail(s: str, n: int = 8000) -> str:
    return s[-n:] if s else ""


@app.post("/render")
def render(req: RenderRequest):
    rid = getattr(getattr(req, "__pydantic_extra__", None), "rid", None)  # best-effort
    # В FastAPI проще взять RID из middleware через request.state, но тут синхронный endpoint.
    # Поэтому логируем без request.state (RID уже есть в middleware-логах), а тут просто добавим локальный.
    local_rid = str(uuid.uuid4())

    t0 = time.perf_counter()
    src = req.source or ""
    mode = _detect_mode(src)
    if mode == "UNSUPPORTED_DRAWMAN":
        raise HTTPException(
            400,
            "DrawMan is not supported in pascal image runner. Use GraphABC (uses GraphABC; ...) for picture tasks.",
        )
    total_timeout = int(req.timeout_seconds or 20)
    total_timeout = max(1, min(120, total_timeout))

    _LOG.debug(
        "RID=%s RENDER start | mode=%s | timeout=%ss | src_len=%s | PABCNETC=%s | screen=%sx%sx%s | trim=%s",
        local_rid,
        mode,
        total_timeout,
        len(src),
        PABCNETC,
        SCREEN_W,
        SCREEN_H,
        SCREEN_D,
        TRIM_DEFAULT,
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pas-") as td:
        td_path = Path(td)
        _LOG.debug("RID=%s tmpdir=%s", local_rid, td)
        src_path = td_path / "main.pas"
        src_path.write_text(src, encoding="utf-8")
        _LOG.debug("RID=%s wrote source: %s bytes", local_rid, src_path.stat().st_size)

        # 1) Compile
        # Leave a bit of time for UI+capture.
        compile_budget = max(6, min(60, total_timeout - 6))
        _LOG.debug("RID=%s COMPILE budget=%ss cmd=%s", local_rid, compile_budget, ["mono", PABCNETC, str(src_path)])
        try:
            cp = subprocess.run(
                ["mono", PABCNETC, str(src_path)],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=compile_budget,
            )
        except subprocess.TimeoutExpired:
            _LOG.debug("RID=%s COMPILE TIMEOUT after %ss", local_rid, compile_budget)
            raise HTTPException(504, "compile timeout")

        _LOG.debug(
            "RID=%s COMPILE done rc=%s stdout_len=%s", local_rid, cp.returncode, len(cp.stdout or "")
        )
        if cp.stdout:
            _LOG.debug("RID=%s COMPILE stdout_tail:\n%s", local_rid, _tail(cp.stdout, 12000))

        if cp.returncode != 0:
            _LOG.debug("RID=%s COMPILE FAILED", local_rid)
            raise HTTPException(400, "compile failed:\n" + _tail(cp.stdout))

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            # Fallback: pick first exe
            exes = list(td_path.glob("*.exe"))
            if not exes:
                _LOG.debug("RID=%s COMPILE ok but no exe in %s", local_rid, td)
                raise HTTPException(500, "compile succeeded but no .exe produced")
            exe_path = exes[0]

        _LOG.debug("RID=%s exe_path=%s size=%s", local_rid, exe_path, exe_path.stat().st_size)

        # 2) Run under Xvfb + UI automation + capture
        compile_sec = time.perf_counter() - t0
        remaining = max(3.0, float(total_timeout) - compile_sec - 0.5)
        run_budget = int(max(3, min(110, remaining)))

        _LOG.debug(
            "RID=%s RUN budget=%ss | compile_sec=%.3f | remaining=%.3f",
            local_rid,
            run_budget,
            compile_sec,
            remaining,
        )

        out_png = td_path / "out.png"
        program_log = td_path / "program.log"

        _LOG.debug("RID=%s ui_runner start | mode=%s", local_rid, mode)

        try:
            run_ui_and_capture(
                exe_path=exe_path,
                out_png=out_png,
                program_log=program_log,
                mode=mode,
                xvfb_screen=f"{SCREEN_W}x{SCREEN_H}x{SCREEN_D}",
                timeout_seconds=run_budget,
                trim=TRIM_DEFAULT,
                log_prefix=f"RID={local_rid}",
            )
        except RuntimeError as e:
            # Provide logs in body to help debugging.
            log_txt = ""
            try:
                if program_log.exists():
                    log_txt = program_log.read_text(encoding="utf-8", errors="replace")
            except Exception:
                log_txt = ""
            msg = f"runtime error: {e}\n\nprogram.log:\n{_tail(log_txt)}"
            _LOG.debug("RID=%s ui_runner ERROR: %s", local_rid, e)
            if log_txt:
                _LOG.debug("RID=%s program.log tail:\n%s", local_rid, _tail(log_txt, 12000))
            raise HTTPException(400, msg)

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_txt = ""
            try:
                if program_log.exists():
                    log_txt = program_log.read_text(encoding="utf-8", errors="replace")
            except Exception:
                log_txt = ""
            _LOG.debug("RID=%s out.png missing/empty | exists=%s size=%s", local_rid, out_png.exists(), out_png.stat().st_size if out_png.exists() else -1)
            if log_txt:
                _LOG.debug("RID=%s program.log tail:\n%s", local_rid, _tail(log_txt, 12000))
            raise HTTPException(400, "failed to capture image (out.png empty)\n\n" + _tail(log_txt))

        png_bytes = out_png.read_bytes()
        dt = time.perf_counter() - t0
        _LOG.debug("RID=%s OK | png_bytes=%s | total_sec=%.3f", local_rid, len(png_bytes), dt)
        return Response(content=png_bytes, media_type="image/png")
