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
    timeout_seconds: int = Field(20, ge=1, le=60)


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
AFTER_ENTER_DELAY_DEFAULT = float(os.getenv("TF_AFTER_ENTER_DELAY", "10.0"))
WINDOW_WAIT_SECONDS_DEFAULT = float(os.getenv("TF_WINDOW_WAIT_SECONDS", "10.0"))


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 4000) -> str:
    return s[-n:] if s else ""


@app.post("/render")
def render(req: RenderRequest):
    src_lower = (req.source or "").lower()
    needs_enter = ("uses drawman" in src_lower) or ("drawman;" in src_lower)

    run_timeout = int(req.timeout_seconds or 1)
    if run_timeout < 1:
        run_timeout = 1

    after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, run_timeout - 1.0))
    capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, run_timeout - 0.5))

    win_wait_seconds = max(WINDOW_WAIT_SECONDS_DEFAULT, min(15.0, run_timeout * 0.7))
    win_wait_seconds = min(win_wait_seconds, max(1.0, run_timeout - 1.0))

    print(
        f"[runner] needs_enter={needs_enter} timeout={run_timeout}s "
        f"capture_delay={capture_delay}s after_enter_delay={after_enter_delay}s win_wait={win_wait_seconds}s"
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(req.source, encoding="utf-8")

        # Compile
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

        # Run in Xvfb and (if DrawMan) smash Enter in many ways.
        inner_script = f"""#!/usr/bin/env bash
set -euo pipefail

cd "{td}"

mono "{exe_path}" > program.log 2>&1 &
pid=$!

# let GUI initialize
sleep 0.7

NEEDS_ENTER={'1' if needs_enter else '0'}
WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

log() {{
  echo "[runner] $*"
}}

show_visible_windows() {{
  log "visible windows (id -> title):"
  for w in $(xdotool search --onlyvisible --name ".*" 2>/dev/null | tail -n 25 || true); do
    title=$(xdotool getwindowname "$w" 2>/dev/null || true)
    echo "[runner]   $w -> $title"
  done
}}

# Pick best window among list: prefer "Чертежник", then "Поле", else first
pick_window_by_title() {{
  local wins="$1"
  local w=""
  local title=""
  local best=""
  local best2=""

  for w in $wins; do
    title="$(xdotool getwindowname "$w" 2>/dev/null || true)"
    echo "[runner]   candidate: $w -> $title"
    if echo "$title" | grep -qi "Чертежник"; then
      best="$w"
      break
    fi
    if [ -z "$best2" ] && echo "$title" | grep -qi "Поле"; then
      best2="$w"
    fi
  done

  if [ -n "$best" ]; then
    echo "$best"
    return
  fi
  if [ -n "$best2" ]; then
    echo "$best2"
    return
  fi
  echo "$(echo "$wins" | awk 'NR==1{{print $1}}')"
}}

focus_and_click() {{
  local win="$1"
  xdotool windowactivate "$win" 2>/dev/null || true
  xdotool windowfocus "$win" 2>/dev/null || true
  # click inside canvas
  xdotool mousemove --window "$win" 120 120 click 1 2>/dev/null || true
  sleep 0.08
}}

# "Million ways" to press Enter
blast_enter() {{
  local win="$1"

  # 1) direct key on window
  xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true

  # 2) keydown/keyup
  xdotool keydown --window "$win" --clearmodifiers Return 2>/dev/null || true
  sleep 0.03
  xdotool keyup   --window "$win" --clearmodifiers Return 2>/dev/null || true

  xdotool keydown --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true
  sleep 0.03
  xdotool keyup   --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true

  # 3) send to focused window (no --window)
  xdotool key --clearmodifiers Return 2>/dev/null || true
  xdotool key --clearmodifiers KP_Enter 2>/dev/null || true

  # 4) small delayed sequences (sometimes Mono misses the first)
  xdotool key --window "$win" --delay 120 --clearmodifiers Return Return KP_Enter 2>/dev/null || true

  # 5) type newline (may be ignored but cheap)
  xdotool type --window "$win" --clearmodifiers $'\\n' 2>/dev/null || true
  xdotool type --clearmodifiers $'\\n' 2>/dev/null || true
}}

# Click likely "Пуск (Enter)" area (bottom-left button strip)
click_start_area() {{
  local win="$1"
  eval "$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)"

  if [ -n "${{HEIGHT:-}}" ]; then
    local y=$((HEIGHT-25))
    [ "$y" -lt 0 ] && y=10

    # try few x positions around the first button
    xdotool mousemove --window "$win" 60 "$y" click 1 2>/dev/null || true
    xdotool mousemove --window "$win" 80 "$y" click 1 2>/dev/null || true
    xdotool mousemove --window "$win" 100 "$y" click 1 2>/dev/null || true
    log "clicked start area in window $win (y=$y)"
  fi
}}

try_start_drawman() {{
  local win="$1"
  log "trying to start DrawMan in window $win"

  # do several rounds: focus -> enter blast -> click start -> enter blast
  local i=0
  while [ $i -lt 4 ]; do
    focus_and_click "$win"
    blast_enter "$win"
    click_start_area "$win"
    blast_enter "$win"
    sleep 0.15
    i=$((i+1))
  done
}}

find_windows_for_pid() {{
  # wait up to WIN_WAIT seconds; keep polling, because --sync with pid is not always reliable
  local end=$(( $(date +%s) + ${{
WIN_WAIT%.*}} ))
  local wins=""
  while [ $(date +%s) -lt $end ]; do
    wins="$(xdotool search --onlyvisible --pid "$pid" 2>/dev/null || true)"
    if [ -n "$wins" ]; then
      echo "$wins"
      return
    fi
    sleep 0.1
  done
  echo ""
}}

if [ "$NEEDS_ENTER" = "1" ] && command -v xdotool >/dev/null 2>&1; then
  log "needs_enter=1; searching windows by PID=$pid (wait up to $WIN_WAIT s)"

  wins="$(find_windows_for_pid)"
  if [ -z "$wins" ]; then
    log "needs_enter=1 but no windows found by PID"
    show_visible_windows
    sleep "$CAPTURE_DELAY"
  else
    log "windows for PID:"
    win="$(pick_window_by_title "$wins")"
    if [ -n "$win" ]; then
      # also try to start on ALL pid windows, not only chosen (some apps have separate tool windows)
      for w in $wins; do
        try_start_drawman "$w"
      done

      # final focus on best window and wait for drawing
      focus_and_click "$win"
      blast_enter "$win"
      sleep "$AFTER_ENTER"
    else
      log "could not pick a window from PID list"
      sleep "$CAPTURE_DELAY"
    fi
  fi
else
  sleep "$CAPTURE_DELAY"
fi

# Screenshot whole virtual screen
import -window root "{out_png}" >/dev/null 2>&1 || true
convert "{out_png}" -trim +repage "{out_png}" >/dev/null 2>&1 || true

kill "$pid" >/dev/null 2>&1 || true
wait "$pid" >/dev/null 2>&1 || true
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
            print("[runner] run.sh output (tail):\n" + _tail(rp.stdout))

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

        return Response(content=out_png.read_bytes(), media_type="image/png")
