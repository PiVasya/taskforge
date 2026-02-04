import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

from .ui_runner import run_ui_and_capture

app = FastAPI(title="taskforge pascal image runner (GraphABC only)")


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC)")
    timeout_seconds: int = Field(20, ge=1, le=120)


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

TRIM_DEFAULT = os.getenv("TF_TRIM", "1").strip().lower() not in ("0", "false", "no", "off")


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 8000) -> str:
    return s[-n:] if s else ""


def _ensure_graphabc_only(src: str):
    s = (src or "").lower()
    # если явно используют DrawMan — сразу отказываем
    if "uses drawman" in s or "drawman;" in s:
        raise HTTPException(400, "DrawMan не поддерживается в pascal-картинках. Используйте только GraphABC.")


@app.post("/render")
def render(req: RenderRequest):
    src = req.source or ""
    _ensure_graphabc_only(src)

    total_timeout = int(req.timeout_seconds or 20)
    total_timeout = max(1, min(120, total_timeout))

    with tempfile.TemporaryDirectory(prefix="tfr-img-pas-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        src_path.write_text(src, encoding="utf-8")

        # 1) Compile
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
            raise HTTPException(400, "compile failed:\n" + _tail(cp.stdout or ""))

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            exes = list(td_path.glob("*.exe"))
            if not exes:
                raise HTTPException(500, "compile succeeded but no .exe produced")
            exe_path = exes[0]

        # 2) Run + capture
        # Оставляем часть времени на запуск UI и скриншот
        run_budget = max(3, min(110, total_timeout - int(time.time() - time.time()) - 1))
        # фактически budget=total_timeout-1 (без умных расчётов; compile_budget уже ограничен)
        run_budget = max(3, min(110, total_timeout - 1))

        out_png = td_path / "out.png"

        try:
            run_ui_and_capture(
                exe_path=exe_path,
                out_png=out_png,
                xvfb_screen=f"{SCREEN_W}x{SCREEN_H}x{SCREEN_D}",
                timeout_seconds=run_budget,
                trim=TRIM_DEFAULT,
            )
        except RuntimeError as e:
            raise HTTPException(400, f"runtime error: {e}")

        if not out_png.exists() or out_png.stat().st_size == 0:
            raise HTTPException(400, "failed to capture image (out.png empty)")

        return Response(content=out_png.read_bytes(), media_type="image/png")
