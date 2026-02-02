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


def _log(msg: str) -> None:
    # Simple single-line logs (good for docker logs)
    print(f"[pascal-image-runner] {msg}", flush=True)


def _detect_mode(src: str) -> str:
    s = (src or "").lower()
    has_drawman = ("uses drawman" in s) or ("drawman;" in s)
    has_graphabc = ("uses graphabc" in s) or ("graphabc;" in s)

    if has_drawman:
        return "DrawMan"
    if has_graphabc:
        return "GraphABC"
    return "Pascal"


@app.post("/render")
def render(req: RenderRequest):
    """
    Compiles and runs PascalABC.NET code headlessly (Xvfb).
    The user code does NOT need to save any image.
    We capture the virtual screen and return it as out.png.
    """
    t0 = time.perf_counter()

    src_lower = (req.source or "").lower()
    mode = _detect_mode(req.source or "")
    needs_enter = ("uses drawman" in src_lower) or ("drawman;" in src_lower)

    # Time budget for the whole request (compile + run + capture).
    total_timeout = int(req.timeout_seconds or 1)
    if total_timeout < 1:
        total_timeout = 1

    # We will compute actual delays after compilation (because compile time eats the budget).
    after_enter_delay = AFTER_ENTER_DELAY_DEFAULT
    capture_delay = CAPTURE_DELAY_DEFAULT
    win_wait_seconds = WINDOW_WAIT_SECONDS_DEFAULT

    _log(
        f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s code_len={len(req.source or '')}"
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(req.source, encoding="utf-8")

        # Compile (PascalABC.NET)
        t_compile0 = time.perf_counter()
        try:
            # Keep compile timeout within the total request timeout.
            compile_timeout = min(30, max(5, total_timeout - 2))
            cp = subprocess.run(
                ["mono", PABCNETC, str(src_path)],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=compile_timeout,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "compile timeout")
        t_compile1 = time.perf_counter()

        compile_sec = (t_compile1 - t_compile0)
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

        # Recompute remaining budget for execution.
        # Leave a small tail for screenshot + process shutdown.
        remaining = max(1.5, float(total_timeout) - compile_sec - 1.0)
        exec_timeout = int(max(1, remaining))

        # Compute delays so that they fit inside exec_timeout.
        # (Old values could sum to > exec_timeout and cause premature HttpClient timeouts.)
        capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, remaining * 0.10))
        if needs_enter:
            after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, remaining * 0.35))
        else:
            after_enter_delay = 0.0

        win_wait_seconds = min(14.0, max(WINDOW_WAIT_SECONDS_DEFAULT, remaining * 0.35))

        # If sum of waits is still too large, scale them down (keep capture_delay minimal).
        max_wait_total = max(0.5, remaining - 0.3)
        wait_sum = win_wait_seconds + after_enter_delay + capture_delay
        if wait_sum > max_wait_total and wait_sum > 0:
            k = max_wait_total / wait_sum
            win_wait_seconds *= k
            after_enter_delay *= k
            capture_delay *= k

        _log(
            f"budget total={total_timeout}s compile={compile_sec:.2f}s exec={exec_timeout}s "
            f"delays win_wait={win_wait_seconds:.2f}s after_enter={after_enter_delay:.2f}s capture_delay={capture_delay:.2f}s"
        )

        # Run headlessly and capture screenshot.
        # For DrawMan, the drawing starts after the user presses Enter ("Пуск (Enter)").
        # We emulate that via xdotool:
        #   - find window by PID (title-regex can be flaky for Cyrillic)
        #   - activate + click inside (ensure focus)
        #   - send Enter
        #   - click bottom-left start button area (safety net)
        #   - wait for drawing, screenshot
        inner_script = f"""#!/usr/bin/env bash
set -e

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

mono "{exe_path}" > program.log 2>&1 &
pid=$!

# Give GUI a moment to initialize (important for DrawMan/WinForms).
sleep 0.7

NEEDS_ENTER={'1' if needs_enter else '0'}
WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

# Convert float seconds to integer
WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=10; fi

wins=""
win=""

if [ "$NEEDS_ENTER" = "1" ] && command -v xdotool >/dev/null 2>&1; then
  echo "[runner] DrawMan: waiting for windows by pid=$pid up to $WIN_WAIT s"

  end=$(( $(date +%s) + WIN_WAIT_INT ))
  while [ $(date +%s) -lt $end ]; do
    # Prefer PID search (avoids Cyrillic regex issues)
    wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)
    if [ -n "$wins" ]; then break; fi
    sleep 0.1
  done

  # Fallback: any visible windows (rare, but keeps behavior robust)
  if [ -z "$wins" ]; then
    wins=$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)
  fi

  if [ -n "$wins" ]; then
    echo "[runner] visible candidate windows:"
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      echo "[runner]   $w -> $title"
    done

    # Pick best window:
    # - Prefer "Чертежник" (main window)
    # - Otherwise "Поле"
    # - Otherwise first non-empty title
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      case "$title" in *Справка* ) continue;; esac
      case "$title" in *Чертежник* ) win=$w; break;; esac
    done

    if [ -z "$win" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname $w 2>/dev/null || true)
        case "$title" in *Справка* ) continue;; esac
        case "$title" in *Поле* ) win=$w; break;; esac
      done
    fi

    if [ -z "$win" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname $w 2>/dev/null || true)
        if [ -n "$title" ]; then win=$w; break; fi
      done
    fi

    if [ -z "$win" ]; then
      win=$(echo "$wins" | head -n 1)
    fi

    if [ -n "$win" ]; then
      echo "[runner] window chosen: $win"
      echo "[runner] focusing + click inside window"

      xdotool windowactivate $win 2>/dev/null || true
      xdotool windowraise $win 2>/dev/null || true
      xdotool windowfocus $win 2>/dev/null || true
      sleep 0.1

      # Click inside to ensure focus (this is the key part for WinForms)
      xdotool mousemove --window $win 140 120 click 1 2>/dev/null || true
      sleep 0.1

      echo "[runner] sending Enter"
      xdotool key --window $win --clearmodifiers Return 2>/dev/null || true
      xdotool key --window $win --clearmodifiers KP_Enter 2>/dev/null || true

      # Safety net: click near bottom-left (Start button area "Пуск (Enter)")
      geom=$(xdotool getwindowgeometry --shell $win 2>/dev/null || true)
      H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2)
      if [ -n "$H" ]; then
        y=$((H-45))
        echo "[runner] clicking start area (H=$H) y=$y"
        xdotool mousemove --window $win 70 $y click 1 2>/dev/null || true
      fi

      # One more Enter after clicking start area
      xdotool key --window $win --clearmodifiers Return 2>/dev/null || true
      xdotool key --window $win --clearmodifiers KP_Enter 2>/dev/null || true

      echo "[runner] waiting after enter: $AFTER_ENTER s"
      sleep "$AFTER_ENTER"
    else
      echo "[runner] DrawMan: could not choose any window"
      sleep "$CAPTURE_DELAY"
    fi
  else
    echo "[runner] DrawMan: no windows found (pid=$pid); capture anyway"
    sleep "$CAPTURE_DELAY"
  fi
else
  sleep "$CAPTURE_DELAY"
fi

# Screenshot whole virtual screen
import -window root "{out_png}" >/dev/null 2>&1 || true
convert "{out_png}" -trim +repage "{out_png}" >/dev/null 2>&1 || true

# Cleanup process (don't hang container)
kill $pid >/dev/null 2>&1 || true
wait $pid >/dev/null 2>&1 || true
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

        t_run0 = time.perf_counter()
        try:
            rp = subprocess.run(
                cmd,
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                # `timeout_seconds` - общий бюджет (compile+run). Здесь ограничиваемся остатком.
                timeout=max(1, int(total_timeout - compile_sec)),
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "run timeout")
        t_run1 = time.perf_counter()

        run_sec = t_run1 - t_run0

        # Always print runner output tail for debugging (but keep it short)
        if rp.stdout:
            _log("run.sh output (tail):\n" + _tail(rp.stdout))

        _log(f"run done exitCode={rp.returncode} runSec={run_sec:.3f}s")

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

        total_sec = time.perf_counter() - t0
        if mode == "DrawMan":
            _log(f"draw complete: DrawMan render OK totalSec={total_sec:.3f}s")
        elif mode == "GraphABC":
            _log(f"draw complete: GraphABC render OK totalSec={total_sec:.3f}s")
        else:
            _log(f"draw complete: Pascal render OK totalSec={total_sec:.3f}s")

        png_bytes = out_png.read_bytes()
        return Response(content=png_bytes, media_type="image/png")
