import os
import subprocess
import tempfile
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge image pascal runner (PascalABC.NET GraphABC/DrawMan)")

class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Enable verbose logs")


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

# Default timing (can be overridden by env)
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
AFTER_ENTER_DELAY_DEFAULT = float(os.getenv("TF_AFTER_ENTER_DELAY", "10.0"))
WINDOW_WAIT_SECONDS_DEFAULT = float(os.getenv("TF_WINDOW_WAIT_SECONDS", "14.0"))

RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 6000) -> str:
    return s[-n:] if s else ""


def _log(msg: str) -> None:
    print(f"[pascal-image-runner] {msg}", flush=True)


def _detect_mode(src: str) -> str:
    s = (src or "").lower()
    if "uses drawman" in s or "drawman;" in s:
        return "DrawMan"
    if "uses graphabc" in s or "graphabc;" in s:
        return "GraphABC"
    return "Pascal"


def _build_bash_script(
    td: str,
    exe_path: Path,
    out_png: Path,
    needs_enter: bool,
    debug: bool,
    run_timeout: int,
) -> str:
    # Keep delays inside the total budget.
    # Leave ~1s for screenshot + cleanup.
    after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, run_timeout - 1.0))
    capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, run_timeout - 0.5))

    win_wait_seconds = max(3.0, min(WINDOW_WAIT_SECONDS_DEFAULT, run_timeout * 0.7))
    win_wait_seconds = min(win_wait_seconds, max(1.0, run_timeout - 1.0))

    return f"""#!/usr/bin/env bash
set -e

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

mono "{exe_path}" > program.log 2>&1 &
pid=$!

sleep 0.7

NEEDS_ENTER={'1' if needs_enter else '0'}
DEBUG={'1' if debug else '0'}

WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

have() {{ command -v "$1" >/dev/null 2>&1; }}
log() {{ echo "[runner] $*"; }}

shot_root() {{
  local file="$1"
  if have import; then
    import -window root "$file" >/dev/null 2>&1 || true
  fi
}}

trim_png() {{
  local file="$1"
  if have convert; then
    convert "$file" -trim +repage "$file" >/dev/null 2>&1 || true
  fi
}}

wins=""
win=""

if [ "$NEEDS_ENTER" = "1" ] && have xdotool; then
  log "needs_enter=1; waiting for windows (pid=$pid) up to $WIN_WAIT s"

  WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
  if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi

  end=$(( $(date +%s) + WIN_WAIT_INT ))
  while [ $(date +%s) -lt $end ]; do
    wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)

    if [ -z "$wins" ]; then
      wins=$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)
    fi

    if [ -n "$wins" ]; then
      break
    fi

    sleep 0.1
  done

  if [ -n "$wins" ]; then
    log "visible candidate windows:"
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      log "  $w -> $title"
    done

    # Pick best window:
    #  - Prefer one with "Чертежник"
    #  - Otherwise one with "Поле"
    #  - Otherwise first non-empty title
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      case "$title" in
        *Справка* ) continue;;
      esac
      case "$title" in
        *Чертежник* ) win=$w; break;;
      esac
    done

    if [ -z "$win" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname $w 2>/dev/null || true)
        case "$title" in
          *Справка* ) continue;;
        esac
        case "$title" in
          *Поле* ) win=$w; break;;
        esac
      done
    fi

    if [ -z "$win" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname $w 2>/dev/null || true)
        if [ -n "$title" ]; then
          win=$w
          break
        fi
      done
    fi

    if [ -z "$win" ]; then
      win=$(echo "$wins" | head -n 1)
    fi
  fi
fi

if [ "$NEEDS_ENTER" = "1" ] && have xdotool && [ -n "$win" ]; then
  log "window chosen: $win"

  # Focus it (do NOT fail if focus commands fail)
  xdotool windowactivate "$win" 2>/dev/null || true
  xdotool windowraise "$win" 2>/dev/null || true
  xdotool windowfocus "$win" 2>/dev/null || true
  sleep 0.12

  # Click inside to ensure focus
  xdotool mousemove --window "$win" 140 120 click 1 2>/dev/null || true
  sleep 0.12

  log "sending Enter (multiple strategies)"
  xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers ISO_Enter 2>/dev/null || true

  xdotool keydown --window "$win" Return 2>/dev/null || true
  xdotool keyup --window "$win" Return 2>/dev/null || true
  xdotool keydown --window "$win" KP_Enter 2>/dev/null || true
  xdotool keyup --window "$win" KP_Enter 2>/dev/null || true

  xdotool key --clearmodifiers Return 2>/dev/null || true
  xdotool key --clearmodifiers KP_Enter 2>/dev/null || true

  xdotool type --window "$win" --clearmodifiers $'\\n' 2>/dev/null || true

  # Click Start button area guesses near bottom-left (where "Пуск (Enter)" is).
  geom=$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)
  H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)

  if [ -n "$H" ]; then
    y1=$((H-45))
    y2=$((H-55))
    y3=$((H-65))
    y4=$((H-75))
    log "clicking start-area guesses (H=$H): y=$y1,$y2,$y3,$y4"
    xdotool mousemove --window "$win" 70 "$y1" click 1 2>/dev/null || true
    xdotool mousemove --window "$win" 70 "$y2" click 1 2>/dev/null || true
    xdotool mousemove --window "$win" 70 "$y3" click 1 2>/dev/null || true
    xdotool mousemove --window "$win" 70 "$y4" click 1 2>/dev/null || true
  fi

  # Repeat Enter after clicking (often helps)
  xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true

  log "waiting after enter: $AFTER_ENTER s"
  sleep "$AFTER_ENTER"
else
  # Non-DrawMan or no window -> just small delay
  sleep "$CAPTURE_DELAY"
fi

shot_root "{out_png}"
trim_png "{out_png}"

kill $pid >/dev/null 2>&1 || true
wait $pid >/dev/null 2>&1 || true

exit 0
"""


@app.post("/render")
def render(req: RenderRequest):
    src = req.source or ""
    mode = _detect_mode(src)
    needs_enter = mode == "DrawMan"
    debug = bool(req.debug)

    run_timeout = int(req.timeout_seconds or 1)
    if run_timeout < 1:
        run_timeout = 1

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={run_timeout}s debug={debug} codeLen={len(src)}")

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(src, encoding="utf-8")

        # Compile (separate timeout, do NOT eat run budget)
        try:
            cp = subprocess.run(
                ["mono", PABCNETC, str(src_path)],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=60,
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

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            run_timeout=run_timeout,
        )

        run_sh = td_path / "run.sh"
        run_sh.write_text(bash_script, encoding="utf-8")
        os.chmod(run_sh, 0o755)

        cmd = [
            "xvfb-run",
            "-a",
            "-s",
            f"-screen 0 {SCREEN_W}x{SCREEN_H}x{SCREEN_D}",
            str(run_sh),
        ]

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

        if rp.stdout:
            _log("run.sh output (tail):\n" + _tail(rp.stdout))

        if rp.returncode != 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(400, f"runtime error:\n{_tail(rp.stdout)}\n\nprogram.log:\n{_tail(log)}")

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(
                400,
                "failed to capture image (out.png not produced). "
                "Program likely did not open a window / draw anything.\n\n"
                f"program.log:\n{_tail(log)}",
            )

        return Response(content=out_png.read_bytes(), media_type="image/png")
