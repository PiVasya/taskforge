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


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 4000) -> str:
    return s[-n:] if s else ""


@app.post("/render")
def render(req: RenderRequest):
    """
    Compiles and runs PascalABC.NET code headlessly (Xvfb).
    The user code does NOT need to save any image.
    We capture the virtual screen and return it as out.png.

    IMPORTANT: DrawMan обычно НЕ начинает рисовать, пока не нажать "Пуск (Enter)".
    Мы пытаемся эмулировать Enter/клик максимально надёжно, НЕ полагаясь на кириллический title-regex,
    потому что в контейнере часто нет корректных UTF-8 locale и xdotool search --name по кириллице ломается.
    """
    src_lower = (req.source or "").lower()
    needs_enter = ("drawman" in src_lower)

    run_timeout = int(req.timeout_seconds or 1)
    if run_timeout < 1:
        run_timeout = 1

    # Keep delays inside the total budget
    after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, run_timeout - 1.2))
    capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, run_timeout - 0.6))

    # Allow time for window to appear (Mono can be slow)
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

        inner_script = f"""#!/usr/bin/env bash
set -euo pipefail

cd "{td}"

mono "{exe_path}" > program.log 2>&1 &
pid=$!

sleep 0.8

NEEDS_ENTER={'1' if needs_enter else '0'}
WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

log() {{
  echo "[runner] $*"
}}

to_int() {{
  local v="$1"
  local i="${{v%.*}}"
  if [ -z "$i" ]; then i=1; fi
  if ! [[ "$i" =~ ^[0-9]+$ ]]; then i=1; fi
  if [ "$i" -lt 1 ]; then i=1; fi
  echo "$i"
}}

WIN_WAIT_INT="$(to_int "$WIN_WAIT")"
AFTER_ENTER_INT="$(to_int "$AFTER_ENTER")"

show_visible_windows() {{
  log "visible windows (id -> title):"
  for w in $(xdotool search --onlyvisible --name ".*" 2>/dev/null | tail -n 25 || true); do
    title=$(xdotool getwindowname "$w" 2>/dev/null || true)
    echo "[runner]   $w -> $title"
  done
}}

pick_best_window() {{
  local wins="$1"
  local best=""
  local best_area=0
  local best_field=""
  local best_field_area=0

  for w in $wins; do
    local title=""
    title="$(xdotool getwindowname "$w" 2>/dev/null || true)"

    local WIDTH=""
    local HEIGHT=""
    eval "$(xdotool getwindowgeometry --shell "$w" 2>/dev/null || true)"
    if [ -z "${{WIDTH:-}}" ] || [ -z "${{HEIGHT:-}}" ]; then
      continue
    fi
    local area=$((WIDTH*HEIGHT))

    echo "[runner]   candidate: $w area=$area title=$title"

    if echo "$title" | grep -Eq '[0-9]+x[0-9]+'; then
      if [ "$area" -gt "$best_field_area" ]; then
        best_field="$w"
        best_field_area="$area"
      fi
    fi

    if [ "$area" -gt "$best_area" ]; then
      best="$w"
      best_area="$area"
    fi
  done

  if [ -n "$best_field" ]; then
    echo "$best_field"
  else
    echo "$best"
  fi
}}

focus_click() {{
  local win="$1"
  xdotool windowactivate "$win" 2>/dev/null || true
  xdotool windowfocus "$win" 2>/dev/null || true
  xdotool mousemove --window "$win" 140 140 click 1 2>/dev/null || true
  sleep 0.08
}}

blast_enter() {{
  local win="$1"
  xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers ISO_Enter 2>/dev/null || true

  xdotool keydown --window "$win" --clearmodifiers Return 2>/dev/null || true
  sleep 0.03
  xdotool keyup --window "$win" --clearmodifiers Return 2>/dev/null || true

  xdotool key --clearmodifiers Return 2>/dev/null || true
  xdotool key --clearmodifiers KP_Enter 2>/dev/null || true

  xdotool key --window "$win" --delay 120 --clearmodifiers Return Return KP_Enter 2>/dev/null || true
  xdotool type --window "$win" --clearmodifiers $'\\n' 2>/dev/null || true
}}

click_start_area() {{
  local win="$1"
  local WIDTH=""
  local HEIGHT=""
  eval "$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)"
  if [ -n "${{HEIGHT:-}}" ]; then
    local y=$((HEIGHT-25))
    [ "$y" -lt 10 ] && y=10
    xdotool mousemove --window "$win" 60  "$y" click 1 2>/dev/null || true
    xdotool mousemove --window "$win" 85  "$y" click 1 2>/dev/null || true
    xdotool mousemove --window "$win" 110 "$y" click 1 2>/dev/null || true
    log "clicked start area (y=$y)"
  fi
}}

try_start_window() {{
  local win="$1"
  log "try start window=$win"
  local i=0
  while [ $i -lt 5 ]; do
    focus_click "$win"
    blast_enter "$win"
    click_start_area "$win"
    blast_enter "$win"
    sleep 0.15
    i=$((i+1))
  done
}}

find_windows() {{
  local deadline=$(( $(date +%s) + WIN_WAIT_INT ))
  local wins=""

  while [ $(date +%s) -lt "$deadline" ]; do
    wins="$(xdotool search --onlyvisible --pid "$pid" 2>/dev/null || true)"
    if [ -n "$wins" ]; then
      echo "$wins"
      return
    fi
    wins="$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)"
    if [ -n "$wins" ]; then
      echo "$wins"
      return
    fi
    sleep 0.1
  done
  echo ""
}}

if [ "$NEEDS_ENTER" = "1" ] && command -v xdotool >/dev/null 2>&1; then
  log "needs_enter=1; searching windows (wait up to $WIN_WAIT s)"
  wins="$(find_windows)"

  if [ -z "$wins" ]; then
    log "no windows found"
    show_visible_windows
    sleep "$CAPTURE_DELAY"
  else
    log "found windows: $(echo "$wins" | wc -w)"
    best="$(pick_best_window "$wins")"
    if [ -z "$best" ]; then
      log "could not pick best window"
      show_visible_windows
      sleep "$CAPTURE_DELAY"
    else
      try_start_window "$best"

      n=0
      for w in $wins; do
        [ "$w" = "$best" ] && continue
        n=$((n+1))
        [ "$n" -gt 4 ] && break
        try_start_window "$w"
      done

      log "waiting after enter: ${AFTER_ENTER_INT}s"
      sleep "$AFTER_ENTER_INT"
    fi
  fi
else
  sleep "$CAPTURE_DELAY"
fi

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
