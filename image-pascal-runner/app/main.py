import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge image pascal runner (DEBUG DrawMan strategies)")


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Enable verbose strategy logs and pre/post comparisons")


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

# Strategy tuning
ENTER_DIFF_PCT = float(os.getenv("TF_ENTER_DIFF_PCT", "0.15"))        # percent
STEP_SLEEP = float(os.getenv("TF_STEP_SLEEP", "0.45"))                # seconds after each action
AFTER_SUCCESS_SLEEP = float(os.getenv("TF_AFTER_SUCCESS_SLEEP", "2.0"))  # seconds after detected success
WINDOW_WAIT_DEFAULT = float(os.getenv("TF_WINDOW_WAIT", "14"))        # seconds


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
    win_wait: float,
    total_budget: int,
) -> str:
    """
    Bash script runs inside Xvfb.
    For DrawMan:
      - finds window by pid
      - takes PRE screenshot
      - tries strategies sequentially
      - after each: POST screenshot + diff metric
      - first strategy that crosses ENTER_DIFF_PCT is considered success
    """

    # We keep some safety margin inside the overall timeout
    # (python subprocess timeout will still cap it).
    # In debug mode, we need more time (multiple screenshots + compare).
    safe_budget = max(5, int(total_budget * 0.9))

    # NOTE: bash uses ImageMagick tools: import, compare, identify, convert
    return f"""#!/usr/bin/env bash
set -euo pipefail

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

echo "[runner] starting mono exe"
mono "{exe_path}" > program.log 2>&1 &
pid=$!

# GUI warm-up
sleep 0.7

NEEDS_ENTER={'1' if needs_enter else '0'}
DEBUG={'1' if debug else '0'}

WIN_WAIT="{win_wait}"
WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi

THRESH_PCT="{ENTER_DIFF_PCT}"
STEP_SLEEP="{STEP_SLEEP}"
AFTER_SUCCESS="{AFTER_SUCCESS_SLEEP}"

# Helpers
now_s() {{ date +"%H:%M:%S"; }}
log() {{ echo "[runner] $(now_s) $*"; }}

have() {{ command -v "$1" >/dev/null 2>&1; }}

# Try to find windows
wins=""
win=""

if [ "$NEEDS_ENTER" = "1" ] && have xdotool; then
  log "DrawMan mode: waiting for window by pid=$pid up to ${WIN_WAIT_INT}s"
  end=$(( $(date +%s) + WIN_WAIT_INT ))
  while [ $(date +%s) -lt $end ]; do
    wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)
    if [ -z "$wins" ]; then
      # fallback: any visible windows (helps when PID search is flaky)
      wins=$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)
    fi
    if [ -n "$wins" ]; then break; fi
    sleep 0.1
  done

  if [ -n "$wins" ]; then
    log "candidate windows:"
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      log "  $w -> $title"
    done

    # Choose best window
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
  fi
fi

# Screenshot util
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

# Diff util: returns pct difference between two images
diff_pct() {{
  local a="$1"
  local b="$2"
  if ! have compare || ! have identify; then
    echo "0"
    return
  fi
  local w h total ae pct
  w=$(identify -format "%w" "$a" 2>/dev/null || echo "0")
  h=$(identify -format "%h" "$a" 2>/dev/null || echo "0")
  total=$(( w * h ))
  if [ "$total" -le 0 ]; then
    echo "0"
    return
  fi
  ae=$(compare -metric AE "$a" "$b" null: 2>&1 || true)
  # ae might contain text in some cases; keep digits only
  ae=$(echo "$ae" | tr -cd "0-9")
  if [ -z "$ae" ]; then ae="0"; fi
  pct=$(awk -v ae="$ae" -v total="$total" 'BEGIN {{ printf "%.6f", (ae/total)*100.0 }}')
  echo "$pct"
}}

# Focus + click inside window (important for WinForms)
focus_and_click() {{
  local w="$1"
  xdotool windowactivate "$w" 2>/dev/null || true
  xdotool windowraise "$w" 2>/dev/null || true
  xdotool windowfocus "$w" 2>/dev/null || true
  sleep 0.12
  xdotool mousemove --window "$w" 140 120 click 1 2>/dev/null || true
  sleep 0.12
}}

# Strategy runner: does action, makes post shot, computes diff to pre, logs
STR_OK="0"
WINNER=""

run_strategy() {{
  local name="$1"
  local action="$2"
  local pre="$3"
  local post="$4"

  if [ "$STR_OK" = "1" ]; then
    return
  fi

  log "---- strategy: $name"
  # shellcheck disable=SC2086
  eval "$action" || true
  sleep "$STEP_SLEEP"
  shot_root "$post"
  trim_png "$post"

  local pct
  pct=$(diff_pct "$pre" "$post")
  # numeric compare using awk
  local pass
  pass=$(awk -v p="$pct" -v t="$THRESH_PCT" 'BEGIN {{ if (p+0 >= t+0) print 1; else print 0; }}')

  log "result: diffPct=$pct threshold=$THRESH_PCT pass=$pass"

  if [ "$pass" = "1" ]; then
    STR_OK="1"
    WINNER="$name"
    log "✅ SUCCESS by strategy: $WINNER"
  fi
}}

# If not DrawMan or no window -> just capture after small delay
if [ "$NEEDS_ENTER" != "1" ] || ! have xdotool || [ -z "$win" ]; then
  if [ "$NEEDS_ENTER" = "1" ]; then
    log "DrawMan: window not found or xdotool missing. win='$win' xdotool=$(have xdotool && echo yes || echo no)"
  else
    log "Non-DrawMan mode: capture after short delay"
  fi

  sleep 0.9
  shot_root "{out_png}"
  trim_png "{out_png}"
  kill $pid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
  exit 0
fi

log "DrawMan: chosen window=$win"
focus_and_click "$win"

# PRE shot
PRE="{td}/pre.png"
shot_root "$PRE"
trim_png "$PRE"
if [ "$DEBUG" = "1" ]; then
  log "PRE screenshot saved: $PRE"
fi

# Gather geometry for click guesses
geom=$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)
H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)
if [ -z "$H" ]; then H="0"; fi

# POST files
POST1="{td}/post1.png"
POST2="{td}/post2.png"
POST3="{td}/post3.png"
POST4="{td}/post4.png"
POST5="{td}/post5.png"
POST6="{td}/post6.png"
POST7="{td}/post7.png"
POST8="{td}/post8.png"
POST9="{td}/post9.png"
POST10="{td}/post10.png"
POST11="{td}/post11.png"
POST12="{td}/post12.png"
POST13="{td}/post13.png"

# --- Strategies (ordered) ---
run_strategy "A1 key Return to window" "xdotool key --window $win --clearmodifiers Return 2>/dev/null" "$PRE" "$POST1"
run_strategy "A2 key KP_Enter to window" "xdotool key --window $win --clearmodifiers KP_Enter 2>/dev/null" "$PRE" "$POST2"
run_strategy "A3 key ISO_Enter to window" "xdotool key --window $win --clearmodifiers ISO_Enter 2>/dev/null" "$PRE" "$POST3"

run_strategy "B1 keydown/keyup Return" "xdotool keydown --window $win Return 2>/dev/null; xdotool keyup --window $win Return 2>/dev/null" "$PRE" "$POST4"
run_strategy "B2 keydown/keyup KP_Enter" "xdotool keydown --window $win KP_Enter 2>/dev/null; xdotool keyup --window $win KP_Enter 2>/dev/null" "$PRE" "$POST5"

run_strategy "C1 key Return to active window" "xdotool key --clearmodifiers Return 2>/dev/null" "$PRE" "$POST6"
run_strategy "C2 key KP_Enter to active window" "xdotool key --clearmodifiers KP_Enter 2>/dev/null" "$PRE" "$POST7"

run_strategy "D1 type newline" "xdotool type --window $win --clearmodifiers $'\\n' 2>/dev/null" "$PRE" "$POST8"

# Click guesses (Start button near bottom-left)
if [ "$H" -gt 0 ]; then
  y1=$((H-45)); y2=$((H-55)); y3=$((H-65)); y4=$((H-75))
else
  y1=700; y2=690; y3=680; y4=670
fi

run_strategy "E1 click start area y1" "xdotool mousemove --window $win 70 $y1 click 1 2>/dev/null" "$PRE" "$POST9"
run_strategy "E2 click start area y2" "xdotool mousemove --window $win 70 $y2 click 1 2>/dev/null" "$PRE" "$POST10"
run_strategy "E3 click start area y3" "xdotool mousemove --window $win 70 $y3 click 1 2>/dev/null" "$PRE" "$POST11"
run_strategy "E4 click start area y4" "xdotool mousemove --window $win 70 $y4 click 1 2>/dev/null" "$PRE" "$POST12"

# Combo: click + enter
run_strategy "F1 click y1 + Return" "xdotool mousemove --window $win 70 $y1 click 1 2>/dev/null; xdotool key --window $win --clearmodifiers Return 2>/dev/null" "$PRE" "$POST13"

if [ "$STR_OK" = "1" ]; then
  log "Waiting AFTER_SUCCESS=${AFTER_SUCCESS}s then final capture"
  sleep "$AFTER_SUCCESS"
else
  log "❌ No strategy crossed threshold. Capturing anyway."
  sleep 0.6
fi

# Final capture
shot_root "{out_png}"
trim_png "{out_png}"

log "final out.png: {out_png}"
log "winner: $WINNER (ok=$STR_OK)"

# Cleanup
kill $pid >/dev/null 2>&1 || true
wait $pid >/dev/null 2>&1 || true

exit 0
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

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s debug={debug} codeLen={len(src)}")

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(src, encoding="utf-8")

        # Compile
        t_compile0 = time.perf_counter()
        try:
            compile_timeout = min(60, max(6, total_timeout - 4))
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

        if cp.returncode != 0:
            raise HTTPException(400, f"compile failed:\n{_tail(cp.stdout)}")

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            exes = list(td_path.glob("*.exe"))
            if exes:
                exe_path = exes[0]
            else:
                raise HTTPException(500, "compile succeeded but no .exe produced")

        # Remaining time for run under our python timeout
        remaining = max(3.0, float(total_timeout) - compile_sec - 0.7)
        run_timeout = int(max(3, remaining))

        win_wait = min(WINDOW_WAIT_DEFAULT, max(4.0, remaining * 0.6))

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            win_wait=win_wait,
            total_budget=run_timeout,
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
            _log("run.sh output (tail):\n" + _tail(rp.stdout))

        _log(f"run done exitCode={rp.returncode} runSec={run_sec:.3f}s")

        if rp.returncode != 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(400, f"runtime error:\n{_tail(rp.stdout)}\n\nprogram.log:\n{_tail(log)}")

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(400, "failed to capture image (out.png not produced)\n\n" + _tail(log))

        total_sec = time.perf_counter() - t0
        _log(f"OK mode={mode} totalSec={total_sec:.3f}s -> returning png bytes")

        return Response(content=out_png.read_bytes(), media_type="image/png")
