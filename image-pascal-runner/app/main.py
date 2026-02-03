import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge pascal image runner (GraphABC / DrawMan)")

# ----------------------------
# Request
# ----------------------------
class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Verbose runner logs")


# ----------------------------
# Env / settings
# ----------------------------
PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

# timings
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
AFTER_ENTER_DELAY_DEFAULT = float(os.getenv("TF_AFTER_ENTER_DELAY", "10.0"))
WINDOW_WAIT_SECONDS_DEFAULT = float(os.getenv("TF_WINDOW_WAIT_SECONDS", "10.0"))

# trim + keys
# (в идеальном файле trim делался всегда; здесь управляемо env, но default=1 чтобы было как “идеально”)
TRIM_DEFAULT = os.getenv("TF_TRIM", "1").strip()  # 1/0
# в идеальном файле xdotool key использовался всегда; здесь можно вырубить, если mono падает
SEND_KEYS_DEFAULT = os.getenv("TF_SEND_KEYS", "1").strip()  # 1/0


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


def _build_ideal_bash_script(
    td: str,
    exe_path: Path,
    out_png: Path,
    needs_enter: bool,
    debug: bool,
    capture_delay: float,
    after_enter_delay: float,
    win_wait_seconds: float,
    do_trim: bool,
    send_keys: bool,
) -> str:
    """
    Это 1-в-1 логика взаимодействия из ideal_runner.py:
    - окно по pid (или fallback любые окна)
    - выбираем "Чертежник" -> "Поле" -> non-empty -> first
    - focus + click inside
    - Enter multiple strategies (если SEND_KEYS=1)
    - клики по зоне "Пуск (Enter)" внизу слева
    - ждём AFTER_ENTER
    - import -window root + (опционально) trim
    """
    return f"""#!/usr/bin/env bash
set -e

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

NEEDS_ENTER={'1' if needs_enter else '0'}
DEBUG={'1' if debug else '0'}
DO_TRIM={'1' if do_trim else '0'}
SEND_KEYS={'1' if send_keys else '0'}

WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=10; fi

now_s() {{ date +"%H:%M:%S"; }}
log() {{ echo "[runner] $(now_s) $*"; }}

log "needs_enter=$NEEDS_ENTER debug=$DEBUG do_trim=$DO_TRIM send_keys=$SEND_KEYS"
log "timeout vars: capture_delay=$CAPTURE_DELAY after_enter=$AFTER_ENTER win_wait=$WIN_WAIT"

mono "{exe_path}" > program.log 2>&1 &
pid=$!

sleep 0.7

wins=""
win=""

if [ "$NEEDS_ENTER" = "1" ] && command -v xdotool >/dev/null 2>&1; then
  log "needs_enter=1; waiting for windows (pid=$pid) up to $WIN_WAIT s"

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

    if [ -n "$win" ]; then
      log "window chosen: $win"

      xdotool windowactivate $win 2>/dev/null || true
      xdotool windowraise $win 2>/dev/null || true
      xdotool windowfocus $win 2>/dev/null || true
      sleep 0.1

      xdotool mousemove --window $win 140 120 click 1 2>/dev/null || true
      sleep 0.1

      if [ "$SEND_KEYS" = "1" ]; then
        log "sending Enter (multiple strategies)"

        xdotool key --window $win --clearmodifiers Return 2>/dev/null || true
        xdotool key --window $win --clearmodifiers KP_Enter 2>/dev/null || true
        xdotool key --window $win --clearmodifiers ISO_Enter 2>/dev/null || true

        xdotool keydown --window $win Return 2>/dev/null || true
        xdotool keyup --window $win Return 2>/dev/null || true
        xdotool keydown --window $win KP_Enter 2>/dev/null || true
        xdotool keyup --window $win KP_Enter 2>/dev/null || true

        xdotool key --clearmodifiers Return 2>/dev/null || true
        xdotool key --clearmodifiers KP_Enter 2>/dev/null || true

        xdotool type --window $win --clearmodifiers $'\\n' 2>/dev/null || true
      else
        log "SEND_KEYS=0 -> skipping key strategies"
      fi

      geom=$(xdotool getwindowgeometry --shell $win 2>/dev/null || true)
      H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2)
      if [ -n "$H" ]; then
        y1=$((H-45))
        y2=$((H-55))
        y3=$((H-65))
        y4=$((H-75))
        log "clicking start-area guesses (H=$H): y=$y1,$y2,$y3,$y4"
        xdotool mousemove --window $win 70 $y1 click 1 2>/dev/null || true
        xdotool mousemove --window $win 70 $y2 click 1 2>/dev/null || true
        xdotool mousemove --window $win 70 $y3 click 1 2>/dev/null || true
        xdotool mousemove --window $win 70 $y4 click 1 2>/dev/null || true
      fi

      if [ "$SEND_KEYS" = "1" ]; then
        xdotool key --window $win --clearmodifiers Return 2>/dev/null || true
        xdotool key --window $win --clearmodifiers KP_Enter 2>/dev/null || true
      fi

      log "waiting after enter: $AFTER_ENTER s"
      sleep "$AFTER_ENTER"
    else
      log "needs_enter=1 but could not choose any window"
      sleep "$CAPTURE_DELAY"
    fi
  else
    log "needs_enter=1 but window list is empty"
    sleep "$CAPTURE_DELAY"
  fi
else
  sleep "$CAPTURE_DELAY"
fi

import -window root "{out_png}" >/dev/null 2>&1 || true
if [ "$DO_TRIM" = "1" ] && command -v convert >/dev/null 2>&1; then
  convert "{out_png}" -trim +repage "{out_png}" >/dev/null 2>&1 || true
fi

kill $pid >/dev/null 2>&1 || true
wait $pid >/dev/null 2>&1 || true
"""


@app.post("/render")
def render(req: RenderRequest):
    t0 = time.perf_counter()
    src = req.source or ""
    mode = _detect_mode(src)
    needs_enter = mode == "DrawMan"
    debug = bool(req.debug)

    total_timeout = int(req.timeout_seconds or 1)
    if total_timeout < 1:
        total_timeout = 1

    do_trim = TRIM_DEFAULT not in ("0", "false", "False", "")
    send_keys = SEND_KEYS_DEFAULT not in ("0", "false", "False", "")

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s debug={debug} codeLen={len(src)}")
    _log(
        f"env: PABCNETC={PABCNETC} TF_SCREEN={SCREEN_W}x{SCREEN_H}x{SCREEN_D} "
        f"TF_TRIM={TRIM_DEFAULT} TF_SEND_KEYS={SEND_KEYS_DEFAULT}"
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(src, encoding="utf-8")

        # Compile timeout: оставляем время на run
        t_compile0 = time.perf_counter()
        try:
            compile_timeout = min(60, max(6, total_timeout - 6))
            _log(f"compile: mono pabcnetc ... timeout={compile_timeout}s")
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

        compile_sec = time.perf_counter() - t_compile0
        _log(f"compile done exitCode={cp.returncode} compileSec={compile_sec:.3f}s")
        if debug and cp.stdout:
            _log("compile output (tail):\n" + _tail(cp.stdout))

        if cp.returncode != 0:
            raise HTTPException(400, f"compile failed:\n{_tail(cp.stdout)}")

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            exes = list(td_path.glob("*.exe"))
            if exes:
                exe_path = exes[0]
            else:
                raise HTTPException(500, "compile succeeded but no .exe produced")

        # Run budget (остальное время)
        remaining = max(3.0, float(total_timeout) - compile_sec - 0.8)
        run_timeout = int(max(3, remaining))

        # параметры как в идеале, но держим в пределах бюджета
        after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, remaining - 0.8))
        capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, remaining - 0.5))
        win_wait_seconds = max(WINDOW_WAIT_SECONDS_DEFAULT, min(14.0, remaining * 0.7))
        win_wait_seconds = min(win_wait_seconds, max(1.0, remaining - 0.8))

        _log(
            f"timing: run_timeout={run_timeout}s capture_delay={capture_delay}s "
            f"after_enter_delay={after_enter_delay}s win_wait={win_wait_seconds}s"
        )

        bash_script = _build_ideal_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            capture_delay=capture_delay,
            after_enter_delay=after_enter_delay,
            win_wait_seconds=win_wait_seconds,
            do_trim=do_trim,
            send_keys=send_keys,
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

        _log(f"run: xvfb-run ... timeout={run_timeout}s")
        t_run0 = time.perf_counter()
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

        run_sec = time.perf_counter() - t_run0
        if rp.stdout:
            _log("run.sh stdout (tail):\n" + _tail(rp.stdout))
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
                "failed to capture image (out.png not produced)\n\n" + _tail(log),
            )

        total_sec = time.perf_counter() - t0
        png_bytes = out_png.read_bytes()
        _log(f"OK mode={mode} totalSec={total_sec:.3f}s -> returning png bytes size={len(png_bytes)}")
        return Response(content=png_bytes, media_type="image/png")
