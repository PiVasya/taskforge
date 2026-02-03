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

# --- DrawMan accel: click "Шаг" many times (NO keyboard!)
STEP_BURST_DEFAULT = int(os.getenv("TF_STEP_BURST", "220"))
STEP_CLICK_DELAY = float(os.getenv("TF_STEP_CLICK_DELAY", "0.015"))
AFTER_START_DEFAULT = float(os.getenv("TF_AFTER_START", "0.8"))

# --- Bottom bar sweep (click whole bottom strip to find real responsive zones)
BAR_SWEEP_ENABLED = os.getenv("TF_BAR_SWEEP", "1").strip() not in ("0", "false", "False", "")
BAR_SWEEP_STEPS = int(os.getenv("TF_BAR_SWEEP_STEPS", "18"))  # how many points across width
BAR_SWEEP_SLEEP = float(os.getenv("TF_BAR_SWEEP_SLEEP", "0.07"))  # small pause between clicks

# --- Optional drag on speed zone (if you want "водил")
BAR_DRAG_ENABLED = os.getenv("TF_BAR_DRAG", "0").strip() not in ("0", "false", "False", "")
BAR_DRAG_SLEEP = float(os.getenv("TF_BAR_DRAG_SLEEP", "0.10"))

# --- Stability wait (image stops changing)
STABLE_PCT = float(os.getenv("TF_STABLE_DIFF_PCT", "0.005"))  # percent
STABLE_NEED = int(os.getenv("TF_STABLE_NEED", "3"))
STABLE_SLEEP = float(os.getenv("TF_STABLE_SLEEP", "0.7"))
STABLE_MAX_SEC = float(os.getenv("TF_STABLE_MAX", "12.0"))  # 0 disables


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
    run_budget_sec: int,
) -> str:
    """
    Bash runs inside Xvfb.
    DrawMan:
      - finds windows by pid (fallback: any visible)
      - chooses main window + start/step windows by title
      - OPTIONAL: sweep bottom bar (click across width) and log diff%
      - Start (mouse)
      - burst STEP clicks (mouse)
      - stability wait (bounded by deadline)
      - final screenshot
      - robust cleanup (kill process-group, no hangs)
    """

    # IMPORTANT: this is inside python f-string -> all literal { } must be doubled
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

RUN_BUDGET="{run_budget_sec}"

BAR_SWEEP={'1' if BAR_SWEEP_ENABLED else '0'}
BAR_SWEEP_STEPS="{BAR_SWEEP_STEPS}"
BAR_SWEEP_SLEEP="{BAR_SWEEP_SLEEP}"

BAR_DRAG={'1' if BAR_DRAG_ENABLED else '0'}
BAR_DRAG_SLEEP="{BAR_DRAG_SLEEP}"

WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1); if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi
STABLE_MAX_INT=$(echo "$STABLE_MAX" | cut -d. -f1); if [ -z "$STABLE_MAX_INT" ]; then STABLE_MAX_INT=0; fi
RUN_BUDGET_INT=$(echo "$RUN_BUDGET" | tr -cd "0-9"); if [ -z "$RUN_BUDGET_INT" ]; then RUN_BUDGET_INT=20; fi

START_TS=$(date +%s)
DEADLINE=$((START_TS + RUN_BUDGET_INT))

time_left() {{
  local now
  now=$(date +%s)
  echo $((DEADLINE - now))
}}

deadline_check_or_exit() {{
  local left
  left=$(time_left)
  if [ "$left" -le 1 ]; then
    log "DEADLINE reached (left=${{left}}s). forcing final capture + exit"
    return 1
  fi
  return 0
}}

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

kill_group_soft_hard() {{
  local pg="$1"
  if [ -z "$pg" ]; then return; fi

  # TERM
  kill -TERM -- -"$pg" >/dev/null 2>&1 || true
  sleep 0.25

  # wait small bounded
  local end
  end=$(( $(date +%s) + 2 ))
  while kill -0 "$pg" >/dev/null 2>&1; do
    if [ $(date +%s) -ge $end ]; then
      break
    fi
    sleep 0.15
  done

  # KILL
  kill -KILL -- -"$pg" >/dev/null 2>&1 || true

  # final bounded wait
  end=$(( $(date +%s) + 2 ))
  while kill -0 "$pg" >/dev/null 2>&1; do
    if [ $(date +%s) -ge $end ]; then
      break
    fi
    sleep 0.15
  done
}}

log "===== runner start ====="
log "needs_enter=$NEEDS_ENTER debug=$DEBUG do_trim=$DO_TRIM"
log "timeouts: WIN_WAIT=$WIN_WAIT AFTER_START=$AFTER_START CAPTURE_DELAY=$CAPTURE_DELAY RUN_BUDGET=$RUN_BUDGET"
log "step: burst=$STEP_BURST clickDelay=$STEP_CLICK_DELAY"
log "bar: sweep=$BAR_SWEEP steps=$BAR_SWEEP_STEPS sleep=$BAR_SWEEP_SLEEP drag=$BAR_DRAG"
log "stable: pct=$STABLE_PCT need=$STABLE_NEED sleep=$STABLE_SLEEP max=$STABLE_MAX"
log "tools: xdotool=$(have xdotool && echo yes || echo no) import=$(have import && echo yes || echo no) convert=$(have convert && echo yes || echo no) identify=$(have identify && echo yes || echo no) compare=$(have compare && echo yes || echo no)"

# Run mono in new session -> kill process-group safely
setsid mono "{exe_path}" > program.log 2>&1 &
pid=$!
pgid=$pid
log "mono pid=$pid (pgid=$pgid)"

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
  kill_group_soft_hard "$pgid"
  exit 0
fi

if ! have xdotool; then
  log "xdotool missing; cannot control DrawMan. capture anyway."
  sleep "$CAPTURE_DELAY"
  shot_root "{out_png}"
  trim_png "{out_png}"
  kill_group_soft_hard "$pgid"
  exit 0
fi

# Find windows
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

# Fallback: any visible windows
if [ -z "$wins" ]; then
  log "DrawMan: pid-search empty -> fallback any visible windows"
  wins=$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)
fi

if [ -z "$wins" ]; then
  log "DrawMan: no windows found at all. capture anyway."
  sleep "$CAPTURE_DELAY"
  shot_root "{out_png}"
  trim_png "{out_png}"
  kill_group_soft_hard "$pgid"
  exit 0
fi

log "candidate windows:"
for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)
  log "  $w -> $title"
done

# pick main/start/step by titles
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
  main=$(echo "$wins" | head -n 1)
fi

log "chosen: main=$main start_btn=$start_btn step_btn=$step_btn"
focus_and_click "$main"

geom=$(xdotool getwindowgeometry --shell "$main" 2>/dev/null || true)
W=$(echo "$geom" | grep '^WIDTH=' | cut -d= -f2 || true)
H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)
if [ -z "$W" ]; then W="0"; fi
if [ -z "$H" ]; then H="0"; fi
log "main geometry: W=$W H=$H"

# ----------------------------
# BAR SWEEP (click whole bottom strip and log diffs)
# ----------------------------
if [ "$BAR_SWEEP" = "1" ] && have compare && have identify && [ "$W" -gt 0 ] && [ "$H" -gt 0 ]; then
  log "BAR_SWEEP: enabled"
  base="{td}/bar_base.png"
  cur="{td}/bar_cur.png"
  shot_root "$base"; trim_png "$base"

  y=$((H-38))
  # safe margins
  x0=10
  x1=$((W-10))
  if [ "$x1" -le "$x0" ]; then x1=$((W-1)); fi

  steps=$BAR_SWEEP_STEPS
  if [ "$steps" -lt 4 ]; then steps=4; fi

  bestPct="0.000000"
  bestX="$x0"

  i=0
  while [ $i -le "$steps" ]; do
    if ! deadline_check_or_exit; then break; fi
    x=$(( x0 + ( (x1-x0) * i / steps ) ))
    xdotool mousemove --window "$main" "$x" "$y" click 1 2>/dev/null || true
    sleep "$BAR_SWEEP_SLEEP"
    shot_root "$cur"; trim_png "$cur"

    pct=$(diff_pct "$base" "$cur")
    log "BAR_SWEEP: i=$i x=$x y=$y diffPct=$pct"

    # keep best
    pass=$(awk -v p="$pct" -v b="$bestPct" 'BEGIN {{ if (p+0 > b+0) print 1; else print 0; }}')
    if [ "$pass" = "1" ]; then
      bestPct="$pct"
      bestX="$x"
    fi

    # move base forward (so we see incremental reaction too)
    cp "$cur" "$base" >/dev/null 2>&1 || true

    i=$((i+1))
  done

  log "BAR_SWEEP: bestX=$bestX bestDiffPct=$bestPct"
else
  log "BAR_SWEEP: skipped (need compare+identify and geometry)"
fi

# optional DRAG (simulate "водил" along bottom bar)
if [ "$BAR_DRAG" = "1" ] && [ "$W" -gt 0 ] && [ "$H" -gt 0 ]; then
  log "BAR_DRAG: enabled"
  y=$((H-38))
  x_from=$((W-180))
  x_to=$((W-20))
  if [ "$x_from" -lt 20 ]; then x_from=20; fi
  if [ "$x_to" -le "$x_from" ]; then x_to=$((W-20)); fi

  log "BAR_DRAG: ($x_from,$y) -> ($x_to,$y)"
  xdotool mousemove --window "$main" "$x_from" "$y" mousedown 1 2>/dev/null || true
  sleep "$BAR_DRAG_SLEEP"
  xdotool mousemove --window "$main" "$x_to" "$y" 2>/dev/null || true
  sleep "$BAR_DRAG_SLEEP"
  xdotool mouseup 1 2>/dev/null || true
  sleep 0.15
fi

# ----------------------------
# START (mouse only)
# ----------------------------
if [ -n "$start_btn" ]; then
  log "clicking Start by window-id: $start_btn"
  click_window "$start_btn"
else
  # fallback: click in bottom-left area of main
  if [ "$H" -le 0 ]; then H="463"; fi
  y1=$((H-45)); y2=$((H-55)); y3=$((H-65)); y4=$((H-75))
  log "fallback start clicks in main: x=70,110 y=$y1,$y2,$y3,$y4"
  for x in 70 110; do
    for y in $y1 $y2 $y3 $y4; do
      xdotool mousemove --window "$main" "$x" "$y" click 1 2>/dev/null || true
      sleep 0.06
    done
  done
fi

sleep "$AFTER_START"

# ----------------------------
# STEP BURST (mouse only, bounded by deadline)
# ----------------------------
if [ -n "$step_btn" ]; then
  log "STEP_BURST: clicking step window-id=$step_btn times=$STEP_BURST"
  i=0
  while [ $i -lt "$STEP_BURST" ]; do
    if ! deadline_check_or_exit; then break; fi
    xdotool click --window "$step_btn" 1 2>/dev/null || true
    i=$((i+1))
    sleep "$STEP_CLICK_DELAY"
  done
  log "STEP_BURST: done i=$i"
else
  log "STEP_BURST: step button not found, skipping"
fi

sleep "$CAPTURE_DELAY"

# ----------------------------
# STABILITY WAIT (optional, bounded by deadline)
# ----------------------------
if [ "$STABLE_MAX_INT" -gt 0 ] && have compare && have identify; then
  log "stability wait enabled (max=$STABLE_MAX s)"
  stableCnt=0
  startT=$(date +%s)

  prev="{td}/stab_prev.png"
  cur="{td}/stab_cur.png"

  shot_root "$prev"; trim_png "$prev"

  while true; do
    if ! deadline_check_or_exit; then break; fi

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

# ----------------------------
# FINAL CAPTURE
# ----------------------------
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

# Cleanup (no hangs)
kill_group_soft_hard "$pgid"

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

    step_burst = int(STEP_BURST_DEFAULT)
    after_start = float(AFTER_START_DEFAULT)

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s debug={debug} codeLen={len(src)}")
    _log(
        f"env: PABCNETC={PABCNETC} TF_SCREEN={SCREEN_W}x{SCREEN_H}x{SCREEN_D} "
        f"TF_TRIM={TRIM_DEFAULT} stepBurst={step_burst} stable(max={STABLE_MAX_SEC}) "
        f"barSweep={'1' if BAR_SWEEP_ENABLED else '0'} barDrag={'1' if BAR_DRAG_ENABLED else '0'}"
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
        # We give bash its own internal budget (run_budget_sec) a bit smaller than python timeout,
        # so bash can always exit cleanly and not hang until python kills it.
        remaining = max(10.0, float(total_timeout) - compile_sec - 1.0)
        run_timeout = int(max(10, remaining))
        run_budget_sec = max(6, int(run_timeout - 2))

        win_wait = min(WINDOW_WAIT_DEFAULT, max(8.0, remaining * 0.5))

        # Make AFTER_START tiny; draw happens via step-burst + stability loop
        if after_start > remaining - 3.0:
            after_start = max(0.3, remaining - 3.0)

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
            run_budget_sec=run_budget_sec,
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

        _log(f"run: xvfb-run ... timeout={run_timeout}s (innerBudget={run_budget_sec}s)")
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
        except subprocess.TimeoutExpired as e:
            # Best-effort: if out.png already exists, return it instead of hard 504
            out = (e.stdout or "") if hasattr(e, "stdout") else ""
            if out:
                _log("run.sh stdout (tail, timeout):\n" + _tail(out))
            if out_png.exists() and out_png.stat().st_size > 0:
                _log("run timeout, but out.png exists -> returning best-effort image")
                return Response(content=out_png.read_bytes(), media_type="image/png")
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
