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


@app.post("/render")
def render(req: RenderRequest):
    """
    Compiles and runs PascalABC.NET code headlessly (Xvfb).
    The user code does NOT need to save any image.
    We capture the virtual screen and return it as out.png.
    """

    src_lower = (req.source or "").lower()
    needs_enter = ("uses drawman" in src_lower) or ("drawman;" in src_lower)

    # Time budget for the whole run (compile+run+capture) in this request.
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

        # Run headlessly and capture screenshot.
        # For DrawMan, the drawing starts after the user presses Enter ("Пуск (Enter)").
        # We emulate that via xdotool using multiple strategies.
        inner_script = f"""#!/usr/bin/env bash
set -e

export LANG=\"{RUN_LANG}\"
export LC_ALL=\"{RUN_LC_ALL}\"

cd \"{td}\"

mono \"{exe_path}\" > program.log 2>&1 &
pid=$!

# Give GUI a moment to initialize (important for DrawMan/WinForms).
sleep 0.7

NEEDS_ENTER={'1' if needs_enter else '0'}
WIN_WAIT=\"{win_wait_seconds}\"
AFTER_ENTER=\"{after_enter_delay}\"
CAPTURE_DELAY=\"{capture_delay}\"

# Convert float seconds to integer (no bash parameter braces; must stay f-string safe)
WIN_WAIT_INT=$(echo \"$WIN_WAIT\" | cut -d. -f1)
if [ -z \"$WIN_WAIT_INT\" ]; then WIN_WAIT_INT=10; fi
AFTER_ENTER_INT=$(echo \"$AFTER_ENTER\" | cut -d. -f1)
if [ -z \"$AFTER_ENTER_INT\" ]; then AFTER_ENTER_INT=2; fi

wins=\"\"
win=\"\"

if [ \"$NEEDS_ENTER\" = \"1\" ] && command -v xdotool >/dev/null 2>&1; then
  echo \"[runner] needs_enter=1; waiting for windows (pid=$pid) up to $WIN_WAIT s\"

  end=$(( $(date +%s) + WIN_WAIT_INT ))
  while [ $(date +%s) -lt $end ]; do
    # 1) Prefer PID search (often best, avoids cyrillic regex issues)
    wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)

    # 2) Fallback: any visible windows
    if [ -z \"$wins\" ]; then
      wins=$(xdotool search --onlyvisible --name \".*\" 2>/dev/null || true)
    fi

    if [ -n \"$wins\" ]; then
      break
    fi

    sleep 0.1
  done

  if [ -n \"$wins\" ]; then
    echo \"[runner] visible candidate windows:\"
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      echo \"[runner]   $w -> $title\"
    done

    # Pick best window:
    #  - Prefer one with \"Чертежник\" in title (main window)
    #  - Otherwise one with \"Поле\" (field)
    #  - Otherwise first non-empty title
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      case \"$title\" in
        *Справка* ) continue;;
      esac
      case \"$title\" in
        *Чертежник* ) win=$w; break;;
      esac
    done

    if [ -z \"$win\" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname $w 2>/dev/null || true)
        case \"$title\" in
          *Справка* ) continue;;
        esac
        case \"$title\" in
          *Поле* ) win=$w; break;;
        esac
      done
    fi

    if [ -z \"$win\" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname $w 2>/dev/null || true)
        if [ -n \"$title\" ]; then
          win=$w
          break
        fi
      done
    fi

    if [ -z \"$win\" ]; then
      win=$(echo \"$wins\" | head -n 1)
    fi

    if [ -n \"$win\" ]; then
      echo \"[runner] window chosen: $win\"

      # Focus it (do NOT fail if focus commands fail)
      xdotool windowactivate $win 2>/dev/null || true
      xdotool windowraise $win 2>/dev/null || true
      xdotool windowfocus $win 2>/dev/null || true
      sleep 0.1

      # Click inside to ensure focus
      xdotool mousemove --window $win 140 120 click 1 2>/dev/null || true
      sleep 0.1

      # === Enter strategies ===
      echo \"[runner] sending Enter (multiple strategies)\"

      # Strategy A: key to chosen window
      xdotool key --window $win --clearmodifiers Return 2>/dev/null || true
      xdotool key --window $win --clearmodifiers KP_Enter 2>/dev/null || true
      xdotool key --window $win --clearmodifiers ISO_Enter 2>/dev/null || true

      # Strategy B: keydown/keyup (some WinForms setups are picky)
      xdotool keydown --window $win Return 2>/dev/null || true
      xdotool keyup --window $win Return 2>/dev/null || true
      xdotool keydown --window $win KP_Enter 2>/dev/null || true
      xdotool keyup --window $win KP_Enter 2>/dev/null || true

      # Strategy C: send to currently focused window too
      xdotool key --clearmodifiers Return 2>/dev/null || true
      xdotool key --clearmodifiers KP_Enter 2>/dev/null || true

      # Strategy D: type newline char
      xdotool type --window $win --clearmodifiers $'\n' 2>/dev/null || true

      # === Click Start button area guesses ===
      # We try multiple Y offsets near the bottom-left (where "Пуск (Enter)" button is).
      geom=$(xdotool getwindowgeometry --shell $win 2>/dev/null || true)
      H=$(echo \"$geom\" | grep '^HEIGHT=' | cut -d= -f2)
      if [ -n \"$H\" ]; then
        y1=$((H-45))
        y2=$((H-55))
        y3=$((H-65))
        y4=$((H-75))
        echo \"[runner] clicking start-area guesses (H=$H): y=$y1,$y2,$y3,$y4\"
        xdotool mousemove --window $win 70 $y1 click 1 2>/dev/null || true
        xdotool mousemove --window $win 70 $y2 click 1 2>/dev/null || true
        xdotool mousemove --window $win 70 $y3 click 1 2>/dev/null || true
        xdotool mousemove --window $win 70 $y4 click 1 2>/dev/null || true
      fi

      # Repeat Enter after clicking (often helps)
      xdotool key --window $win --clearmodifiers Return 2>/dev/null || true
      xdotool key --window $win --clearmodifiers KP_Enter 2>/dev/null || true

      echo \"[runner] waiting after enter: $AFTER_ENTER s\"
      sleep \"$AFTER_ENTER\"
    else
      echo \"[runner] needs_enter=1 but could not choose any window\"
      sleep \"$CAPTURE_DELAY\"
    fi
  else
    echo \"[runner] needs_enter=1 but window list is empty\"
    echo \"[runner] visible windows (id -> title):\"
    for w in $(xdotool search --onlyvisible --name \".*\" 2>/dev/null | tail -n 20 || true); do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      echo \"[runner]   $w -> $title\"
    done
    sleep \"$CAPTURE_DELAY\"
  fi
else
  sleep \"$CAPTURE_DELAY\"
fi

# Screenshot whole virtual screen
import -window root \"{out_png}\" >/dev/null 2>&1 || true
convert \"{out_png}\" -trim +repage \"{out_png}\" >/dev/null 2>&1 || true

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

        # IMPORTANT:
        # Do NOT return FileResponse from a TemporaryDirectory: Starlette streams the file later,
        # but the temp folder is deleted right after we return from this function -> 500.
        # Read bytes now and return them.
        png_bytes = out_png.read_bytes()
        return Response(content=png_bytes, media_type="image/png")
