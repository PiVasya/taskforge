import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge pascal image runner (GraphABC/DrawMan)")

# PascalABC.NET console compiler (under Mono)
PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

# Headless screen
SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

# Locale
RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

# Behavior toggles
DO_TRIM = os.getenv("TF_TRIM", "0").strip()  # "1" -> trim out.png
SEND_KEYS = os.getenv("TF_SEND_KEYS", "0").strip()  # "1" -> also send Enter keys for DrawMan

# Timing defaults (can be overridden)
WIN_WAIT_DEFAULT = float(os.getenv("TF_WINDOW_WAIT", "14.0"))         # seconds
AFTER_START_DEFAULT = float(os.getenv("TF_AFTER_START", "10.0"))      # seconds
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))   # seconds (non-DrawMan basic)

# "Wait until stable" settings (helps big drawings)
# diffPct between consecutive screenshots must be <= STABLE_DIFF_PCT for STABLE_NEED times in a row
STABLE_DIFF_PCT = float(os.getenv("TF_STABLE_DIFF_PCT", "0.005"))   # percent, 0.005% is small
STABLE_NEED = int(os.getenv("TF_STABLE_NEED", "3"))                # consecutive stable frames
STABLE_SLEEP = float(os.getenv("TF_STABLE_SLEEP", "0.7"))          # delay between frames
STABLE_MAX = float(os.getenv("TF_STABLE_MAX", "0.0"))              # max seconds to wait; 0 -> auto from timeout


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Verbose runner logs")


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 7000) -> str:
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
    after_start: float,
    capture_delay: float,
    stable_max: float,
) -> str:
    """
    Runs inside Xvfb:
      - starts mono exe in background
      - for DrawMan: finds window, focuses, clicks "Start" area; optionally sends Enter
      - waits for drawing to stabilize (diff between frames drops below threshold)
      - captures out.png from root window
    """

    # If stable_max not specified, we derive it from request timeout indirectly outside;
    # here we just pass a number.
    return f"""#!/usr/bin/env bash
set -u
# do NOT 'set -e': mono may abort; we still want screenshot

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

now_s() {{ date +"%H:%M:%S"; }}
log() {{ echo "[runner] $(now_s) $*"; }}

have() {{ command -v "$1" >/dev/null 2>&1; }}

log "===== runner start ====="
log "needs_enter={'1' if needs_enter else '0'} do_trim={DO_TRIM} send_keys={SEND_KEYS}"
log "timeouts: WIN_WAIT={win_wait} AFTER_START={after_start} CAPTURE_DELAY={capture_delay}"
log "stable: diffPct={STABLE_DIFF_PCT} need={STABLE_NEED} sleep={STABLE_SLEEP} max={stable_max}"
log "tools: xdotool=$(have xdotool && echo yes || echo no) import=$(have import && echo yes || echo no) convert=$(have convert && echo yes || echo no) identify=$(have identify && echo yes || echo no) compare=$(have compare && echo yes || echo no)"

mono "{exe_path}" > program.log 2>&1 &
pid=$!
log "mono pid=$pid"

cleanup() {{
  log "cleanup: kill pid=$pid"
  kill $pid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
}}
trap cleanup EXIT

# GUI warm-up
sleep 0.7

NEEDS_ENTER={'1' if needs_enter else '0'}
DEBUG={'1' if debug else '0'}

WIN_WAIT="{win_wait}"
WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi

AFTER_START="{after_start}"
CAPTURE_DELAY="{capture_delay}"

STABLE_DIFF="{STABLE_DIFF_PCT}"
STABLE_NEED="{STABLE_NEED}"
STABLE_SLEEP="{STABLE_SLEEP}"
STABLE_MAX="{stable_max}"

DO_TRIM="{DO_TRIM}"

# Screenshot utils
shot_root() {{
  local file="$1"
  if have import; then
    import -window root "$file" >/dev/null 2>&1 || true
  fi
}}

trim_png() {{
  local file="$1"
  if [ "$DO_TRIM" = "1" ] && have convert; then
    convert "$file" -trim +repage "$file" >/dev/null 2>&1 || true
  fi
}}

# diff util: percent difference between two PNGs
diff_pct() {{
  local a="$1"
  local b="$2"
  if ! have compare || ! have identify; then
    echo "999"
    return
  fi
  local w h total ae pct
  w=$(identify -format "%w" "$a" 2>/dev/null || echo "0")
  h=$(identify -format "%h" "$a" 2>/dev/null || echo "0")
  total=$(( w * h ))
  if [ "$total" -le 0 ]; then
    echo "999"
    return
  fi
  ae=$(compare -metric AE "$a" "$b" null: 2>&1 || true)
  ae=$(echo "$ae" | tr -cd "0-9")
  if [ -z "$ae" ]; then ae="0"; fi
  pct=$(awk -v ae="$ae" -v total="$total" 'BEGIN {{ printf "%.6f", (ae/total)*100.0 }}')
  echo "$pct"
}}

focus_and_click() {{
  local w="$1"
  xdotool windowactivate "$w" 2>/dev/null || true
  xdotool windowraise "$w" 2>/dev/null || true
  xdotool windowfocus "$w" 2>/dev/null || true
  sleep 0.12
  # click inside to ensure focus
  xdotool mousemove --window "$w" 140 120 click 1 2>/dev/null || true
  sleep 0.12
}}

send_enter() {{
  local w="$1"
  if [ "{SEND_KEYS}" = "1" ]; then
    xdotool key --window "$w" --clearmodifiers Return 2>/dev/null || true
    xdotool key --window "$w" --clearmodifiers KP_Enter 2>/dev/null || true
    xdotool key --clearmodifiers Return 2>/dev/null || true
  fi
}}

click_start_guesses() {{
  local w="$1"
  local h="$2"
  local y1 y2 y3 y4
  if [ "$h" -gt 0 ]; then
    y1=$((h-45)); y2=$((h-55)); y3=$((h-65)); y4=$((h-75))
  else
    y1=700; y2=690; y3=680; y4=670
  fi

  # несколько X-координат тоже, чтобы точнее попасть по "Пуск"
  local xs="70 110 150"
  log "clicking start button guesses: x=$xs y=$y1,$y2,$y3,$y4 (H=$h)"

  for x in $xs; do
    xdotool mousemove --window "$w" $x $y1 click 1 2>/dev/null || true
    xdotool mousemove --window "$w" $x $y2 click 1 2>/dev/null || true
    xdotool mousemove --window "$w" $x $y3 click 1 2>/dev/null || true
    xdotool mousemove --window "$w" $x $y4 click 1 2>/dev/null || true
  done
}}

# PRE screenshot (debug use)
PRE="{td}/pre.png"
shot_root "$PRE"
trim_png "$PRE"
if have identify; then
  log "taking PRE screenshot"
  log "screenshot: $PRE size=$(stat -c%s "$PRE" 2>/dev/null || echo 0) identify='$(identify "$PRE" 2>/dev/null | head -n 1 || true)'"
fi

win=""
wins=""

if [ "$NEEDS_ENTER" = "1" ] && have xdotool; then
  log "DrawMan: waiting windows by pid=$pid up to ${{WIN_WAIT_INT}}.0 s"

  end=$(( $(date +%s) + WIN_WAIT_INT ))
  while [ $(date +%s) -lt $end ]; do
    wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)
    if [ -z "$wins" ]; then
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

    # choose best
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
      win=$(echo "$wins" | head -n 1)
    fi
  fi

  if [ -n "$win" ]; then
    log "DrawMan: chosen window=$win"
    focus_and_click "$win"

    # geometry
    geom=$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)
    H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)
    if [ -z "$H" ]; then H="0"; fi

    # click start guesses + optional enter
    click_start_guesses "$win" "$H"
    send_enter "$win"

    # give time to start
    log "waiting AFTER_START=$AFTER_START s"
    sleep "$AFTER_START"
  else
    log "DrawMan: window not found -> fallback capture after CAPTURE_DELAY=$CAPTURE_DELAY"
    sleep "$CAPTURE_DELAY"
  fi
else
  # not drawman (or no xdotool)
  sleep "$CAPTURE_DELAY"
fi

# --- wait until stable (optional but recommended) ---
# If STABLE_MAX <= 0 -> do small default: 0 (skip) unless DrawMan, then do ~a few tries
if [ "$(echo "$STABLE_MAX <= 0" | awk '{{print ($1?1:0)}}')" = "1" ]; then
  # If user did not set STABLE_MAX, do a short stability wait for DrawMan only
  if [ "$NEEDS_ENTER" = "1" ]; then
    STABLE_MAX="8.0"
  else
    STABLE_MAX="0.0"
  fi
fi

if [ "$(echo "$STABLE_MAX > 0" | awk '{{print ($1?1:0)}}')" = "1" ]; then
  log "stability wait enabled (max=$STABLE_MAX s)"
  local_start=$(date +%s)
  stable_cnt=0

  prev="{td}/stab_prev.png"
  cur="{td}/stab_cur.png"

  shot_root "$prev"
  trim_png "$prev"
  sleep "$STABLE_SLEEP"

  while true; do
    # stop by time
    now=$(date +%s)
    elapsed=$(( now - local_start ))
    # STABLE_MAX may be float; compare via awk
    stop=$(awk -v e="$elapsed" -v m="$STABLE_MAX" 'BEGIN {{ if (e+0 >= m+0) print 1; else print 0; }}')
    if [ "$stop" = "1" ]; then
      log "stability: time limit reached (elapsed=${{elapsed}}s)"
      break
    fi

    shot_root "$cur"
    trim_png "$cur"

    pct=$(diff_pct "$prev" "$cur")
    ok=$(awk -v p="$pct" -v t="$STABLE_DIFF" 'BEGIN {{ if (p+0 <= t+0) print 1; else print 0; }}')

    log "stability: diffPct=$pct threshold=$STABLE_DIFF ok=$ok stableCnt=$stable_cnt/${{STABLE_NEED}}"

    if [ "$ok" = "1" ]; then
      stable_cnt=$((stable_cnt+1))
    else
      stable_cnt=0
    fi

    if [ "$stable_cnt" -ge "$STABLE_NEED" ]; then
      log "stability: ✅ stable reached"
      break
    fi

    mv -f "$cur" "$prev" >/dev/null 2>&1 || true
    sleep "$STABLE_SLEEP"
  done
else
  log "stability wait disabled"
fi

# FINAL capture
shot_root "{out_png}"
trim_png "{out_png}"

if have identify; then
  log "taking FINAL screenshot out.png"
  log "screenshot: {out_png} size=$(stat -c%s "{out_png}" 2>/dev/null || echo 0) identify='$(identify "{out_png}" 2>/dev/null | head -n 1 || true)'"
fi

log "listing artifacts:"
ls -la "{td}" | tail -n 200 || true

log "tail program.log (last 160 lines):"
tail -n 160 program.log 2>/dev/null || true

log "===== runner end (OK) ====="
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

    # Budget split: compile + run
    compile_timeout = min(60, max(6, total_timeout - 6))

    _log(
        f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s debug={debug} codeLen={len(src)}"
    )
    _log(
        f"env: PABCNETC={PABCNETC} TF_SCREEN={SCREEN_W}x{SCREEN_H}x{SCREEN_D} "
        f"TF_TRIM={DO_TRIM} TF_SEND_KEYS={SEND_KEYS} "
        f"stable(pct={STABLE_DIFF_PCT},need={STABLE_NEED},sleep={STABLE_SLEEP},max={STABLE_MAX})"
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        # Always UTF-8: PascalABC.NET нормально ест UTF-8, и у тебя кириллица в комментах
        data = src.encode("utf-8")
        src_path.write_bytes(data)
        _log(f"write source: {src_path} bytes={len(data)}")

        # Compile
        _log(f"compile: mono pabcnetc ... timeout={compile_timeout}s")
        t_compile0 = time.perf_counter()
        try:
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

        if cp.stdout:
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

        # Run timeout = what is left (but not too small)
        remaining = max(6.0, float(total_timeout) - compile_sec - 0.5)
        run_timeout = int(max(6, remaining))

        # Heuristics: for big DrawMan drawings we want more "after start" + stability wait.
        win_wait = min(WIN_WAIT_DEFAULT, max(6.0, remaining * 0.55))
        after_start = min(AFTER_START_DEFAULT, max(2.0, remaining * 0.30)) if needs_enter else 0.0
        capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.4, remaining * 0.06))

        # Stable max: if user didn't set STABLE_MAX (0) we auto: up to ~half remaining for DrawMan
        stable_max = STABLE_MAX
        if stable_max <= 0.0 and needs_enter:
            stable_max = max(6.0, remaining * 0.45)
            # but never exceed run_timeout-2
            stable_max = min(stable_max, max(2.0, float(run_timeout) - 2.0))
        elif stable_max <= 0.0:
            stable_max = 0.0

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            win_wait=win_wait,
            after_start=after_start,
            capture_delay=capture_delay,
            stable_max=stable_max,
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

        # We always try to return out.png even if mono died; but if out.png missing -> error
        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log_txt = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(400, "failed to capture image (out.png not produced)\n\n" + _tail(log_txt))

        total_sec = time.perf_counter() - t0
        _log(f"OK mode={mode} totalSec={total_sec:.3f}s -> returning png bytes size={out_png.stat().st_size}")

        return Response(content=out_png.read_bytes(), media_type="image/png")
