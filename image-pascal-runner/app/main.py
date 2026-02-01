import os
import subprocess
import tempfile
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge image pascal runner (PascalABC.NET GraphABC/DrawMan)")


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (can use GraphABC / DrawMan)")
    timeout_seconds: int = Field(8, ge=1, le=60)


# PascalABC.NET console compiler (under Mono)
PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

# Headless screen size (root screenshot will have this size)
SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

# How long to let the program run before we capture the screen (seconds)
CAPTURE_DELAY = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
AFTER_ENTER_DELAY = float(os.getenv("TF_AFTER_ENTER_DELAY", "1.2"))
WINDOW_WAIT_SECONDS = float(os.getenv("TF_WINDOW_WAIT_SECONDS", "2.0"))


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 4000) -> str:
    return s[-n:] if s else ""


@app.post("/render")
def render(req: RenderRequest):
    '''
    Compiles and runs PascalABC.NET code headlessly (Xvfb).
    The user code does NOT need to save any image.
    We capture the virtual screen and return it as out.png.
    '''
    src_lower = (req.source or "").lower()
    needs_enter = ("uses drawman" in src_lower) or ("drawman;" in src_lower)

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(req.source, encoding="utf-8")

        # Compile (PascalABC.NET)
        try:
            cp = subprocess.run(
                ["mono", PABCNETC, str(src_path)],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=30,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "compile timeout")

        if cp.returncode != 0:
            raise HTTPException(400, f"compile failed:\n{_tail(cp.stdout)}")

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            exes = list(td_path.glob("*.exe"))
            if exes:
                exe_path = exes[0]
            else:
                raise HTTPException(500, "compile succeeded but no .exe produced")

        # Run headlessly and capture screenshot.
        script = f'''
set -euo pipefail
cd "{td}"

xvfb-run -a -s "-screen 0 {SCREEN_W}x{SCREEN_H}x{SCREEN_D}" bash -lc '
  set -euo pipefail
  mono "{exe_path}" > program.log 2>&1 &
  pid=$!

  NEEDS_ENTER={1 if needs_enter else 0}
  if [ "$NEEDS_ENTER" = "1" ] && command -v xdotool >/dev/null 2>&1; then
    win=""
    # Wait a bit for DrawMan window to appear
    for i in $(seq 1 25); do
      win=$(xdotool search --onlyvisible --name "Поле" 2>/dev/null | tail -n 1 || true)
      [ -n "$win" ] || win=$(xdotool search --onlyvisible --name "Чертежник" 2>/dev/null | tail -n 1 || true)
      [ -n "$win" ] || win=$(xdotool search --onlyvisible --name "Исполнитель" 2>/dev/null | tail -n 1 || true)
      [ -n "$win" ] || win=$(xdotool search --onlyvisible 2>/dev/null | tail -n 1 || true)
      if [ -n "$win" ]; then
        break
      fi
      sleep 0.1
    done

    if [ -n "$win" ]; then
      xdotool windowactivate "$win" 2>/dev/null || true
      # Start DrawMan (Run/Stop is bound to Enter)
      xdotool key --window "$win" Return 2>/dev/null || true
    fi

    sleep {AFTER_ENTER_DELAY}
  else
    sleep {CAPTURE_DELAY}
  fi

  import -window root "{out_png}" >/dev/null 2>&1 || true
  convert "{out_png}" -trim +repage "{out_png}" >/dev/null 2>&1 || true

  kill $pid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
'
'''

        try:
            rp = subprocess.run(
                ["bash", "-lc", script],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=req.timeout_seconds,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "run timeout")

        if rp.returncode != 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(
                400,
                f"runtime error:\n{_tail(rp.stdout)}\n\nprogram.log:\n{_tail(log)}",
            )

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(
                400,
                "failed to capture image (out.png not produced). "
                "Your program likely did not open a window / draw anything.\n\n"
                f"program.log:\n{_tail(log)}",
            )

        # IMPORTANT:
        # Do NOT return FileResponse from a TemporaryDirectory: Starlette streams the file later,
        # but the temp folder is deleted right after we return from this function -> 500.
        # Read bytes now and return them.
        png_bytes = out_png.read_bytes()
        return Response(content=png_bytes, media_type="image/png")
