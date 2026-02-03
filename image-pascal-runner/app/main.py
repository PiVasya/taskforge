import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

from .ui_runner import run_ui_and_capture


app = FastAPI(title="taskforge pascal image runner (GraphABC / DrawMan)")


class RenderRequest(BaseModel):
    # PascalABC.NET source code (GraphABC / DrawMan)
    source: str = Field(..., description="PascalABC.NET source code")
    # Runner total budget (compile+run)
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Verbose logs in HTTP error body")


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

TRIM_DEFAULT = os.getenv("TF_TRIM", "1").strip().lower() not in ("0", "false", "no", "off")


@app.get("/health")
def health():
    return {"ok": True}


def _detect_mode(src: str) -> str:
    s = (src or "").lower()
    if "uses drawman" in s or "drawman;" in s:
        return "DrawMan"
    if "uses graphabc" in s or "graphabc;" in s:
        return "GraphABC"
    return "Pascal"


def _tail(s: str, n: int = 8000) -> str:
    return s[-n:] if s else ""


@app.post("/render")
def render(req: RenderRequest):
    t0 = time.perf_counter()
    src = req.source or ""
    mode = _detect_mode(src)
    total_timeout = int(req.timeout_seconds or 20)
    total_timeout = max(1, min(120, total_timeout))

    with tempfile.TemporaryDirectory(prefix="tfr-img-pas-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        src_path.write_text(src, encoding="utf-8")

        # 1) Compile
        # Leave a bit of time for UI+capture.
        compile_budget = max(6, min(60, total_timeout - 6))
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
            raise HTTPException(504, "compile timeout")

        if cp.returncode != 0:
            raise HTTPException(400, "compile failed:\n" + _tail(cp.stdout))

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            # Fallback: pick first exe
            exes = list(td_path.glob("*.exe"))
            if not exes:
                raise HTTPException(500, "compile succeeded but no .exe produced")
            exe_path = exes[0]

        # 2) Run under Xvfb + UI automation + capture
        compile_sec = time.perf_counter() - t0
        remaining = max(3.0, float(total_timeout) - compile_sec - 0.5)
        run_budget = int(max(3, min(110, remaining)))

        out_png = td_path / "out.png"
        program_log = td_path / "program.log"

        try:
            run_ui_and_capture(
                exe_path=exe_path,
                out_png=out_png,
                program_log=program_log,
                mode=mode,
                xvfb_screen=f"{SCREEN_W}x{SCREEN_H}x{SCREEN_D}",
                timeout_seconds=run_budget,
                trim=TRIM_DEFAULT,
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
            raise HTTPException(400, msg)

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_txt = ""
            try:
                if program_log.exists():
                    log_txt = program_log.read_text(encoding="utf-8", errors="replace")
            except Exception:
                log_txt = ""
            raise HTTPException(400, "failed to capture image (out.png empty)\n\n" + _tail(log_txt))

        png_bytes = out_png.read_bytes()
        _ = time.perf_counter() - t0
        return Response(content=png_bytes, media_type="image/png")
