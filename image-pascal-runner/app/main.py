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

# Locale for proper Unicode handling in tools that may depend on it
RUN_LANG = os.getenv("TF_LANG", "en_US.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "en_US.UTF-8")


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
    """
    src_lower = (req.source or "").lower()
    needs_enter = ("uses drawman" in src_lower) or ("drawman;" in src_lower)

    run_timeout = int(req.timeout_seconds or 1)
    if run_timeout < 1:
        run_timeout = 1

    # Keep delays inside the total budget
    after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, run_timeout - 1.0))
    capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, run_timeout - 0.5))

    # Allow more time for window to appear (DrawMan/Mono can be slow)
    win_wait_seconds = max(WINDOW_WAIT_SECONDS_DEFAULT, min(12.0, run_timeout * 0.6))
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

        # IMPORTANT FIX:
        # Do NOT search window by title (xdotool has issues with Cyrillic/':' in regex matching in practice).
        # Search by PID of the process and then pick the most relevant window by its title.
        inner_script = f"""#!/usr/bin/env bash
set -euo pipefail

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

mono "{exe_path}" > program.log 2>&1 &
pid=$!

# Let GUI initialize
sleep 0.6

NEEDS_ENTER={'1' if needs_enter else '0'}
WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

pick_window_by_title() {{
  local wins="$1"
  local w=""
  local title=""
  local best=""

  # Prefer "Чертежник" window, then "Поле", otherwise first window
  for w in $wins; do
    title="$(xdotool getwindowname "$w" 2>/dev/null || true)"
    if [ -n "$title" ]; then
      # debug
      echo "[runner]   candidate: $w -> $title"
    else
      echo "[runner]   candidate: $w -> (no title)"
    fi

    if echo "$title" | grep -qi "Чертежник"; then
      best="$w"
      break
    fi

    if [ -z "$best" ] && echo "$title" | grep -qi "Поле"; then
      best="$w"
      # keep searching for "Чертежник"
    fi
  done

  if [ -z "$best" ]; then
    best="$(echo "$wins" | awk 'NR==1{{print $1}}')"
  fi

  echo "$best"
}}

send_enter_and_start() {{
  local win="$1"

  echo "[runner] window chosen: $win"
  xdotool windowactivate "$win" 2>/dev/null || true
  xdotool windowfocus "$win" 2>/dev/null || true

  # click inside window to ensure focus
  xdotool mousemove --window "$win" 120 120 click 1 2>/dev/null || true
  sleep 0.1

  # multiple variants of Enter
  xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers "Return" 2>/dev/null || true
  xdotool key --window "$win" --clearmodifiers "KP_Enter" 2>/dev/null || true

  # extra: try sending to the currently focused window too
  xdotool key --clearmodifiers Return 2>/dev/null || true
  xdotool key --clearmodifiers KP_Enter 2>/dev/null || true

  # additionally click the "Пуск" button area (bottom-left toolbar)
  # (works even if hotkey doesn't)
  eval "$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)"
  if [ -n "${{HEIGHT:-}}" ]; then
    y=$((HEIGHT-25))
    [ "$y" -lt 0 ] && y=10
    xdotool mousemove --window "$win" 70 "$y" click 1 2>/dev/null || true
    echo "[runner] clicked start area in window $win"
  fi
}}

if [ "$NEEDS_ENTER" = "1" ] && command -v xdotool >/dev/null 2>&1; then
  echo "[runner] needs_enter=1; searching windows by PID=$pid (wait up to ${{
WIN_WAIT}}s)"

  # Wait for any window created by this PID (avoid title search completely)
  wins="$(timeout "${{WIN_WAIT}}s" xdotool search --sync --onlyvisible --pid "$pid" 2>/dev/null || true)"

  if [ -z "$wins" ]; then
    echo "[runner] needs_enter=1 but no windows found by PID"
    echo "[runner] visible windows (id -> title):"
    for w in $(xdotool search --onlyvisible --name ".*" 2>/dev/null | tail -n 20 || true); do
      title=$(xdotool getwindowname "$w" 2>/dev/null || true)
      echo "[runner]   $w -> $title"
    done
    sleep "$CAPTURE_DELAY"
  else
    echo "[runner] windows for PID:"
    win="$(pick_window_by_title "$wins")"
    if [ -n "$win" ]; then
      send_enter_and_start "$win"
      # give time for drawing
      sleep "$AFTER_ENTER"
    else
      echo "[runner] could not pick a window from PID list"
      sleep "$CAPTURE_DELAY"
    fi
  fi
else
  sleep "$CAPTURE_DELAY"
fi

# Screenshot whole virtual screen
import -window root "{out_png}" >/dev/null 2>&1 || true
convert "{out_png}" -trim +repage "{out_png}" >/dev/null 2>&1 || true

# Cleanup process (don't hang container)
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

        png_bytes = out_png.read_bytes()
        return Response(content=png_bytes, media_type="image/png")
