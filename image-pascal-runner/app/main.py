import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge pascal image runner (GraphABC / DrawMan)")


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

WINDOW_WAIT_DEFAULT = float(os.getenv("TF_WINDOW_WAIT", "14.0"))
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
TRIM_DEFAULT = os.getenv("TF_TRIM", "0").strip()  # 1/0

# --- DrawMan accel: click "Шаг" many times (no keyboard!)
STEP_BURST_DEFAULT = int(os.getenv("TF_STEP_BURST", "220"))          # how many clicks on "Шаг"
STEP_CLICK_DELAY = float(os.getenv("TF_STEP_CLICK_DELAY", "0.015")) # delay between clicks
AFTER_START_DEFAULT = float(os.getenv("TF_AFTER_START", "0.8"))      # small pause after clicking "Пуск"

# --- Stability wait (image stops changing)
STABLE_PCT = float(os.getenv("TF_STABLE_DIFF_PCT", "0.005"))  # percent
STABLE_NEED = int(os.getenv("TF_STABLE_NEED", "3"))           # how many stable samples in a row
STABLE_SLEEP = float(os.getenv("TF_STABLE_SLEEP", "0.7"))     # seconds between samples
STABLE_MAX_SEC = float(os.getenv("TF_STABLE_MAX", "12.0"))    # max seconds for stability loop (0 disables)

# NOTE: ВАЖНО — НЕ используем xdotool key вообще (оно иногда валит mono/WinForms).
# Всё только мышкой.


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
    capture_delay: float,
    do_trim: bool,
    after_start: float,
    step_burst: int,
    step_click_delay: float,
    stable_pct: float,
    stable_need: int,
    stable_sleep: float,
    stable_max: float,
) -> str:
    # Bash is embedded inside a python f-string -> all literal { } must be doubled.
    return f"""#!/usr/bin/env bash
set -u
set -o pipefail

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

now_s() {{ date +"%H:%M:%S"; }}
log() {{ echo "[runner] $(now_s) $*"; }}
have() {{ command -v "$1" >/dev/null 2>&1; }}

NEEDS_ENTER={'1' if needs_enter else '0'}
DEBUG={'1' if debug else '0'}
DO_TRIM={'1' if do_trim else '0'}

WIN_WAIT="{win_wait}"
CAPTURE_DELAY="{capture_delay}"
AFTER_START="{after_start}"

STEP_BURST="{step_burst}"
STEP_CLICK_DELAY="{step_click_delay}"

STABLE_PCT="{stable_pct}"
STABLE_NEED="{stable_need}"
STABLE_SLEEP="{stable_sleep}"
STABLE_MAX="{stable_max}"

WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1); if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi
STABLE_MAX_INT=$(echo "$STABLE_MAX" | cut -d. -f1); if [ -z "$STABLE_MAX_INT" ]; then STABLE_MAX_INT=0; fi

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

diff_pct() {{
  local a="$1"
  local b="$2"
  if ! have compare || ! have identify; then
    echo "0"
    return
  fi
  local w h total ae
  w=$(identify -format "%w" "$a" 2>/dev/null || echo "0")
  h=$(identify -format "%h" "$a" 2>/dev/null || echo "0")
  total=$(( w * h ))
  if [ "$total" -le 0 ]; then
    echo "0"
    return
  fi
  ae=$(compare -metric AE "$a" "$b" null: 2>&1 || true)
  ae=$(echo "$ae" | tr -cd "0-9")
  if [ -z "$ae" ]; then ae="0"; fi
  awk -v ae="$ae" -v total="$total" 'BEGIN {{ printf "%.6f", (ae/total)*100.0 }}'
}}

focus_and_click() {{
  local w="$1"
  xdotool windowactivate "$w" 2>/dev/null || true
  xdotool windowraise "$w" 2>/dev/null || true
  xdotool windowfocus "$w" 2>/dev/null || true
  sleep 0.12
  xdotool mousemove --window "$w" 140 120 click 1 2>/dev/null || true
  sleep 0.12
}}

click_window() {{
  local w="$1"
  if [ -z "$w" ]; then return; fi
  xdotool windowactivate "$w" 2>/dev/null || true
  xdotool click --window "$w" 1 2>/dev/null || true
}}

log "===== runner start ====="
log "needs_enter=$NEEDS_ENTER do_trim=$DO_TRIM debug=$DEBUG"
log "timeouts: WIN_WAIT=$WIN_WAIT AFTER_START=$AFTER_START CAPTURE_DELAY=$CAPTURE_DELAY"
log "step: burst=$STEP_BURST clickDelay=$STEP_CLICK_DELAY"
log "stable: pct=$STABLE_PCT need=$STABLE_NEED sleep=$STABLE_SLEEP max=$STABLE_MAX"
log "tools: xdotool=$(have xdotool && echo yes || echo no) import=$(have import && echo yes || echo no) convert=$(have convert && echo yes || echo no) identify=$(have identify && echo yes || echo no) compare=$(have compare && echo yes || echo no)"

mono "{exe_path}" > program.log 2>&1 &
pid=$!
log "mono pid=$pid"

sleep 0.7

PRE="{td}/pre.png"
log "taking PRE screenshot"
shot_root "$PRE"
trim_png "$PRE"
log "pre size=$(stat -c%s "$PRE" 2>/dev/null || echo 0)"

# Non-DrawMan: just capture later
if [ "$NEEDS_ENTER" != "1" ]; then
  sleep "$CAPTURE_DELAY"
  shot_root "{out_png}"
  trim_png "{out_png}"
  log "===== runner end (OK non-DrawMan) ====="
  kill $pid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
  exit 0
fi

if ! have xdotool; then
  log "xdotool missing; cannot control DrawMan. capture anyway."
  sleep "$CAPTURE_DELAY"
  shot_root "{out_png}"
  trim_png "{out_png}"
  kill $pid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
  exit 0
fi

wins=""
main=""
start_btn=""
step_btn=""

log "DrawMan: waiting windows by pid=$pid up to $WIN_WAIT s"
end=$(( $(date +%s) + WIN_WAIT_INT ))
while [ $(date +%s) -lt $end ]; do
  wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)
  if [ -n "$wins" ]; then break; fi
  sleep 0.1
done

# Fallback: sometimes --pid search is flaky. Then take any visible windows.
if [ -z "$wins" ]; then
  log "DrawMan: pid-search returned empty. fallback to any visible windows."
  wins=$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)
fi

if [ -z "$wins" ]; then
  log "DrawMan: no windows found at all. capture anyway."
  sleep "$CAPTURE_DELAY"
  shot_root "{out_png}"
  trim_png "{out_png}"
  kill $pid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
  exit 0
fi

log "candidate windows:"
for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)
  log "  $w -> $title"
done

# Find main, start, step by title
for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)
  case "$title" in
    *Справка* ) continue;;
  esac

  case "$title" in
    *Чертежник* ) main=$w;;
  esac

  case "$title" in
    *"Пуск (Enter)"* ) start_btn=$w;;
  esac

  case "$title" in
    *"Шаг (Space)"* ) step_btn=$w;;
  esac
done

if [ -z "$main" ]; then
  # sometimes main title may be empty - pick first window (but not help)
  main=$(echo "$wins" | head -n 1)
fi

log "chosen: main=$main start_btn=$start_btn step_btn=$step_btn"
focus_and_click "$main"

# ---- START (mouse only) ----
if [ -n "$start_btn" ]; then
  log "clicking Start by window-id: $start_btn"
  click_window "$start_btn"
else
  # fallback: click in bottom-left area of main
  geom=$(xdotool getwindowgeometry --shell "$main" 2>/dev/null || true)
  H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)
  if [ -z "$H" ]; then H="463"; fi
  y1=$((H-45)); y2=$((H-55)); y3=$((H-65)); y4=$((H-75))
  log "fallback start clicks in main window: x=70,110 y=$y1,$y2,$y3,$y4 (H=$H)"
  for x in 70 110; do
    for y in $y1 $y2 $y3 $y4; do
      xdotool mousemove --window "$main" "$x" "$y" click 1 2>/dev/null || true
      sleep 0.06
    done
  done
fi

sleep "$AFTER_START"

# ---- ACCEL: spam STEP clicks ----
# This is the key trick: no keyboard, just "Шаг" to force fast drawing.
if [ -n "$step_btn" ]; then
  log "step-burst: clicking step button window-id=$step_btn times=$STEP_BURST"
  i=0
  while [ $i -lt "$STEP_BURST" ]; do
    xdotool click --window "$step_btn" 1 2>/dev/null || true
    i=$((i+1))
    sleep "$STEP_CLICK_DELAY"
  done
else
  log "step button not found; skipping step-burst"
fi

# small buffer
sleep "$CAPTURE_DELAY"

# ---- Stability wait (optional) ----
if [ "$STABLE_MAX_INT" -gt 0 ] && have compare && have identify; then
  log "stability wait enabled (max=$STABLE_MAX s)"
  stableCnt=0
  startT=$(date +%s)

  prev="{td}/stab_prev.png"
  cur="{td}/stab_cur.png"

  shot_root "$prev"; trim_png "$prev"

  while true; do
    sleep "$STABLE_SLEEP"
    shot_root "$cur"; trim_png "$cur"

    pct=$(diff_pct "$prev" "$cur")
    ok=$(awk -v p="$pct" -v t="$STABLE_PCT" 'BEGIN {{ if (p+0 <= t+0) print 1; else print 0; }}')
    log "stability: diffPct=$pct threshold=$STABLE_PCT ok=$ok stableCnt=$stableCnt/$STABLE_NEED"

    if [ "$ok" = "1" ]; then
      stableCnt=$((stableCnt+1))
    else
      stableCnt=0
    fi

    cp "$cur" "$prev" >/dev/null 2>&1 || true

    if [ "$stableCnt" -ge "$STABLE_NEED" ]; then
      log "stability: ✅ stable reached"
      break
    fi

    now=$(date +%s)
    elapsed=$((now - startT))
    if [ "$elapsed" -ge "$STABLE_MAX_INT" ]; then
      log "stability: max reached -> stop waiting"
      break
    fi
  done
fi

# ---- Final screenshot ----
log "taking FINAL screenshot out.png"
shot_root "{out_png}"
trim_png "{out_png}"
log "out size=$(stat -c%s "{out_png}" 2>/dev/null || echo 0)"

# If out looks bad -> use PRE (avoid empty/black)
outSize=$(stat -c%s "{out_png}" 2>/dev/null || echo 0)
preSize=$(stat -c%s "$PRE" 2>/dev/null || echo 0)
if [ "$outSize" -lt 1000 ] && [ "$preSize" -gt "$outSize" ]; then
  log "out.png looks bad (size=$outSize). using pre.png (size=$preSize)"
  cp "$PRE" "{out_png}" >/dev/null 2>&1 || true
fi

# Cleanup (do not fail on nonzero wait)
kill $pid >/dev/null 2>&1 || true
wait $pid >/dev/null 2>&1 || true

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

    do_trim = TRIM_DEFAULT not in ("0", "false", "False", "")
    capture_delay = float(CAPTURE_DELAY_DEFAULT)

    step_burst = STEP_BURST_DEFAULT
    after_start = AFTER_START_DEFAULT

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s debug={debug} codeLen={len(src)}")
    _log(
        f"env: PABCNETC={PABCNETC} TF_SCREEN={SCREEN_W}x{SCREEN_H}x{SCREEN_D} "
        f"TF_TRIM={TRIM_DEFAULT} stepBurst={step_burst} stable(max={STABLE_MAX_SEC})"
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(src, encoding="utf-8")

        # Compile
        t_compile0 = time.perf_counter()
        try:
            compile_timeout = min(60, max(6, total_timeout - 6))
            _log(f"write source: {src_path} bytes={src_path.stat().st_size}")
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

        # Run budget
        remaining = max(10.0, float(total_timeout) - compile_sec - 1.0)
        run_timeout = int(max(10, remaining))

        win_wait = min(WINDOW_WAIT_DEFAULT, max(8.0, remaining * 0.5))

        # Keep AFTER_START tiny; draw happens via step-burst and stability loop
        if after_start > remaining - 2.0:
            after_start = max(0.3, remaining - 2.0)

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            win_wait=win_wait,
            capture_delay=capture_delay,
            do_trim=do_trim,
            after_start=after_start,
            step_burst=step_burst,
            step_click_delay=STEP_CLICK_DELAY,
            stable_pct=STABLE_PCT,
            stable_need=STABLE_NEED,
            stable_sleep=STABLE_SLEEP,
            stable_max=STABLE_MAX_SEC,
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

        # Even if bash returned nonzero, try to return what we captured (best effort)
        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(400, "failed to capture image (out.png not produced)\n\n" + _tail(log))

        total_sec = time.perf_counter() - t0
        png_bytes = out_png.read_bytes()
        _log(f"OK mode={mode} totalSec={total_sec:.3f}s -> returning png bytes size={len(png_bytes)}")
        return Response(content=png_bytes, media_type="image/png")
