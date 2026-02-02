import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge image pascal runner (PascalABC.NET GraphABC/DrawMan)")


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (can use GraphABC / DrawMan)")
    # DrawMan usually starts only after Enter ("Пуск (Enter)")
    timeout_seconds: int = Field(20, ge=1, le=60)


# PascalABC.NET console compiler (under Mono)
PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

# Headless screen size (root screenshot will have this size)
SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

# Default timing (can be overridden by env)
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
AFTER_ENTER_DELAY_DEFAULT = float(os.getenv("TF_AFTER_ENTER_DELAY", "10.0"))
WINDOW_WAIT_SECONDS_DEFAULT = float(os.getenv("TF_WINDOW_WAIT_SECONDS", "10.0"))

# Locale for tools (avoid en_US.UTF-8 if locales are not generated in container)
RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 4000) -> str:
    return s[-n:] if s else ""


def _detect_mode(src_lower: str) -> str:
    # Order matters: DrawMan tasks should be treated as DrawMan even if they also mention GraphABC
    if ("uses drawman" in src_lower) or ("drawman;" in src_lower):
        return "DrawMan"
    if ("uses graphabc" in src_lower) or ("graphabc;" in src_lower):
        return "GraphABC"
    return "Unknown"


def _log(msg: str) -> None:
    # Single prefix, easy to grep in docker logs
    print(f"[pascal-image-runner] {msg}", flush=True)


@app.post("/render")
def render(req: RenderRequest):
    """
    Compiles and runs PascalABC.NET code headlessly (Xvfb).
    The user code does NOT need to save any image.
    We capture the virtual screen and return it as out.png.
    """
    t0 = time.monotonic()

    src = req.source or ""
    src_lower = src.lower()

    mode = _detect_mode(src_lower)
    needs_enter = (mode == "DrawMan")

    run_timeout = int(req.timeout_seconds or 1)
    if run_timeout < 1:
        run_timeout = 1

    # Keep delays inside the total budget.
    # Leave a small tail (~1s) for screenshot + cleanup.
    after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, run_timeout - 1.0))
    capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, run_timeout - 0.5))

    # Window discovery can be slow on Mono/WinForms.
    win_wait_seconds = max(WINDOW_WAIT_SECONDS_DEFAULT, min(14.0, run_timeout * 0.7))
    win_wait_seconds = min(win_wait_seconds, max(1.0, run_timeout - 1.0))

    _log(
        f"start mode={mode} needs_enter={needs_enter} timeout={run_timeout}s "
        f"capture_delay={capture_delay:.2f}s after_enter_delay={after_enter_delay:.2f}s win_wait={win_wait_seconds:.2f}s "
        f"code_len={len(src)}"
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(src, encoding="utf-8")

        # --- Compile ---
        t_compile0 = time.monotonic()
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

        compile_sec = time.monotonic() - t_compile0
        _log(f"compile done exitCode={cp.returncode} compileSec={compile_sec:.3f}s")

        if cp.returncode != 0:
            raise HTTPException(400, f"compile failed:\n{_tail(cp.stdout)}")

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            exes = list(td_path.glob("*.exe"))
            if exes:
                exe_path = exes[0]
            else:
                raise HTTPException(500, "compile succeeded but no .exe produced")

        # --- Run headlessly and capture screenshot ---
        # For DrawMan, the drawing starts after user presses Enter ("Пуск (Enter)").
        # We emulate that via xdotool:
        #   1) find windows (prefer PID search)
        #   2) choose best window (Чертежник > Поле > first titled)
        #   3) focus + click (CRITICAL)
        #   4) send Enter + click "Пуск" area (bottom-left)
        #   5) wait after_enter_delay, then screenshot
        inner_script = f"""#!/usr/bin/env bash
set -e

log() {{ echo "[pascal-image-runner][runner] $1"; }}

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

start_ts=$(date +%s)

log "run start mode={mode} needs_enter={'1' if needs_enter else '0'}"

mono "{exe_path}" > program.log 2>&1 &
pid=$!

log "spawned mono pid=$pid"
sleep 0.7

NEEDS_ENTER={'1' if needs_enter else '0'}
WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

# Convert float seconds to integer (portable)
WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
[ -z "$WIN_WAIT_INT" ] && WIN_WAIT_INT=10

wins=""
win=""

pick_window() {{
  local wins_list="$1"
  local w=""
  local title=""

  # Prefer main window "Чертежник", skip help
  for w in $wins_list; do
    title=$(xdotool getwindowname "$w" 2>/dev/null || true)
    case "$title" in
      *Справка* ) continue;;
    esac
    case "$title" in
      *Чертежник* ) echo "$w"; return 0;;
    esac
  done

  # Then prefer "Поле"
  for w in $wins_list; do
    title=$(xdotool getwindowname "$w" 2>/dev/null || true)
    case "$title" in
      *Справка* ) continue;;
    esac
    case "$title" in
      *Поле* ) echo "$w"; return 0;;
    esac
  done

  # Then first with non-empty title
  for w in $wins_list; do
    title=$(xdotool getwindowname "$w" 2>/dev/null || true)
    if [ -n "$title" ]; then
      echo "$w"; return 0
    fi
  done

  # Fallback: just first
  echo "$wins_list" | head -n 1
}}

if [ "$NEEDS_ENTER" = "1" ] && command -v xdotool >/dev/null 2>&1; then
  log "DrawMan: waiting for windows by pid=$pid up to ${WIN_WAIT}s"

  end=$(( $(date +%s) + WIN_WAIT_INT ))
  while [ $(date +%s) -lt $end ]; do
    wins=$(xdotool search --onlyvisible --pid "$pid" 2>/dev/null || true)
    [ -n "$wins" ] && break
    sleep 0.1
  done

  if [ -z "$wins" ]; then
    log "DrawMan: no windows found by PID; falling back to global visible windows"
    wins=$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)
  fi

  if [ -n "$wins" ]; then
    log "visible candidate windows:"
    for w in $wins; do
      title=$(xdotool getwindowname "$w" 2>/dev/null || true)
      log "  $w -> $title"
    done

    win=$(pick_window "$wins")
    if [ -n "$win" ]; then
      log "window chosen: $win"
      xdotool windowactivate "$win" 2>/dev/null || true
      xdotool windowraise "$win" 2>/dev/null || true
      xdotool windowfocus "$win" 2>/dev/null || true
      sleep 0.1

      # CRITICAL: click inside the window so WinForms actually receives keyboard
      xdotool mousemove --window "$win" 140 120 click 1 2>/dev/null || true
      sleep 0.1

      log "sending Enter"
      xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
      xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true
      sleep 0.2

      # Click "Пуск (Enter)" area (bottom-left). One robust guess.
      geom=$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)
      H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2)
      if [ -n "$H" ]; then
        y=$((H-55))
        [ "$y" -lt 10 ] && y=10
        log "clicking start-area (H=$H) x=70 y=$y"
        xdotool mousemove --window "$win" 70 "$y" click 1 2>/dev/null || true
      fi

      # One more Enter after click (cheap, helps sometimes)
      xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true

      log "waiting after enter: ${AFTER_ENTER}s"
      sleep "$AFTER_ENTER"
    else
      log "could not choose a window from list"
      sleep "$CAPTURE_DELAY"
    fi
  else
    log "window list empty; just sleeping capture_delay"
    sleep "$CAPTURE_DELAY"
  fi
else
  # GraphABC or unknown: usually draws immediately
  log "no Enter needed; sleeping capture_delay=${CAPTURE_DELAY}s"
  sleep "$CAPTURE_DELAY"
fi

log "capturing screenshot"
import -window root "{out_png}" >/dev/null 2>&1 || true
convert "{out_png}" -trim +repage "{out_png}" >/dev/null 2>&1 || true

log "cleanup: killing pid=$pid"
kill "$pid" >/dev/null 2>&1 || true
wait "$pid" >/dev/null 2>&1 || true

end_ts=$(date +%s)
dur=$(( end_ts - start_ts ))
log "run finished seconds=${dur}"
"""

        run_sh = td_path / "run.sh"
        run_sh.write_text(inner_script, encoding="utf-8")
        os.chmod(run_sh, 0o755)

        cmd = [
            "xvfb-run",
            "-a",
            "-s",
            f"-screen 0 {SCREEN_W}x{SCREEN_H}x{SCREEN_D}",
            str(run_sh),
        ]

        t_run0 = time.monotonic()
        try:
            rp = subprocess.run(
                cmd,
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=run_timeout,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "run timeout")
        run_sec = time.monotonic() - t_run0

        if rp.stdout:
            _log("runner output (tail):\n" + _tail(rp.stdout))

        _log(f"run done exitCode={rp.returncode} runSec={run_sec:.3f}s")

        if rp.returncode != 0:
            log_path = td_path / "program.log"
            log_txt = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(
                400,
                f"runtime error:\n{_tail(rp.stdout)}\n\nprogram.log:\n{_tail(log_txt)}",
            )

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log_txt = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(
                400,
                "failed to capture image (out.png not produced). "
                "Your program likely did not open a window / draw anything.\n\n"
                f"program.log:\n{_tail(log_txt)}",
            )

        total_sec = time.monotonic() - t0
        _log(f"done mode={mode} totalSec={total_sec:.3f}s (compile={compile_sec:.3f}s run={run_sec:.3f}s)")

        # IMPORTANT:
        # Do NOT return FileResponse from a TemporaryDirectory: Starlette streams the file later,
        # but the temp folder is deleted right after we return from this function -> 500.
        # Read bytes now and return them.
        png_bytes = out_png.read_bytes()
        return Response(content=png_bytes, media_type="image/png")
