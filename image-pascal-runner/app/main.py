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
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.6"))

# trim (можно включить, но основное — мы теперь скриним ОКНО, а не root)
TRIM_DEFAULT = os.getenv("TF_TRIM", "1").strip()  # 1/0

# ВАЖНО: клавиши могут валить mono/WinForms, поэтому по умолчанию выключено
SEND_KEYS_DEFAULT = os.getenv("TF_SEND_KEYS", "0").strip()  # 1/0

# Burst кликов по "Шаг" (мышью)
STEP_BURST_DEFAULT = int(os.getenv("TF_STEP_BURST", "200"))
STEP_DELAY_MS_DEFAULT = int(os.getenv("TF_STEP_DELAY_MS", "8"))  # xdotool --delay in ms

# Дифф-лог после каждого действия
DIFF_LOG_DEFAULT = os.getenv("TF_DIFF_LOG", "1").strip()  # 1/0
DIFF_THRESH_PCT = float(os.getenv("TF_DIFF_THRESH_PCT", "0.05"))  # просто для логов


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
    if "uses graphabc" in s or "uses graphabc;" in s or "graphabc;" in s:
        return "GraphABC"
    return "Pascal"


def _build_bash_script(
    td: str,
    exe_path: Path,
    out_png: Path,
    needs_enter: bool,
    debug: bool,
    win_wait: float,
    do_trim: bool,
    send_keys: bool,
    step_burst: int,
    step_delay_ms: int,
    capture_delay: float,
    diff_log: bool,
    run_budget_sec: int,
) -> str:
    # bash embedded in f-string => all literal { } must be doubled
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
SEND_KEYS={'1' if send_keys else '0'}
DIFF_LOG={'1' if diff_log else '0'}

WIN_WAIT="{win_wait}"
CAPTURE_DELAY="{capture_delay}"
STEP_BURST="{step_burst}"
STEP_DELAY_MS="{step_delay_ms}"
RUN_BUDGET="{run_budget_sec}"

WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1); if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi

# DEADLINE (чтобы не улетать в 504)
start_ts=$(date +%s)
deadline=$((start_ts + RUN_BUDGET))

deadline_left() {{
  local now
  now=$(date +%s)
  echo $((deadline - now))
}}

check_deadline_or_exit() {{
  local left
  left=$(deadline_left)
  if [ "$left" -le 1 ]; then
    log "DEADLINE reached (left=${{left}}s). forcing final capture + exit"
    return 1
  fi
  return 0
}}

# Screenshot helpers
shot_window() {{
  local wid="$1"
  local file="$2"
  if have import; then
    if [ -n "$wid" ]; then
      import -window "$wid" "$file" >/dev/null 2>&1 || true
    else
      import -window root "$file" >/dev/null 2>&1 || true
    fi
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

# Start mono in its own process group (so we can kill everything)
setsid mono "{exe_path}" > program.log 2>&1 &
pid=$!
pgid=$pid

log "===== runner start ====="
log "needs_enter=$NEEDS_ENTER debug=$DEBUG do_trim=$DO_TRIM send_keys=$SEND_KEYS diff_log=$DIFF_LOG"
log "timeouts: WIN_WAIT=$WIN_WAIT CAPTURE_DELAY=$CAPTURE_DELAY RUN_BUDGET=$RUN_BUDGET"
log "step: burst=$STEP_BURST delayMs=$STEP_DELAY_MS"
log "tools: xdotool=$(have xdotool && echo yes || echo no) import=$(have import && echo yes || echo no) convert=$(have convert && echo yes || echo no) identify=$(have identify && echo yes || echo no) compare=$(have compare && echo yes || echo no)"
log "mono pid=$pid (pgid=$pgid)"

sleep 0.7

# If not DrawMan => just capture something (window will be in root usually)
if [ "$NEEDS_ENTER" != "1" ] || ! have xdotool; then
  sleep "$CAPTURE_DELAY"
  shot_window "" "{out_png}"
  trim_png "{out_png}"
  kill -TERM -$pgid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
  log "===== runner end (non-DrawMan) ====="
  exit 0
fi

# ---- window discovery ----
wins=""
log "DrawMan: waiting windows by pid=$pid up to $WIN_WAIT s"
end=$(( $(date +%s) + WIN_WAIT_INT ))
while [ $(date +%s) -lt $end ]; do
  wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)
  if [ -n "$wins" ]; then break; fi
  # ранний fallback (чтобы не терять 14 секунд)
  wins=$(xdotool search --onlyvisible --name ".*" 2>/dev/null || true)
  if [ -n "$wins" ]; then break; fi
  sleep 0.12
done

if [ -z "$wins" ]; then
  log "DrawMan: no windows found at all. capture root."
  shot_window "" "{out_png}"
  trim_png "{out_png}"
  kill -TERM -$pgid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
  exit 0
fi

log "candidate windows:"
for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)
  log "  $w -> $title"
done

# Find start/step buttons by title
start_btn=""
step_btn=""
for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)
  case "$title" in
    *"Пуск (Enter)"*) start_btn=$w ;;
    *"Шаг (Space)"*) step_btn=$w ;;
  esac
done

# Choose REAL main window:
# - exclude tiny controls (buttons/labels)
# - choose max area window that contains "Исполнитель" or "Чертежник"
main=""
best_area=0

is_control_title() {{
  local t="$1"
  case "$t" in
    *"Пуск (Enter)"*|*"Шаг (Space)"*|*"Выход (Esc)"*|*"Справка (F1)"*|*"Скорость"*|*"Состояние"*|*"Шаг:"*|*"Поле "* ) return 0 ;;
  esac
  return 1
}}

for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)

  # skip help / obvious controls
  if is_control_title "$title"; then
    continue
  fi
  case "$title" in
    *Справка* ) continue ;;
  esac

  geom=$(xdotool getwindowgeometry --shell "$w" 2>/dev/null || true)
  W=$(echo "$geom" | grep '^WIDTH=' | cut -d= -f2 || echo 0)
  H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || echo 0)
  area=$((W*H))

  # ignore ultra-small (лейблы)
  if [ "$area" -lt 50000 ]; then
    continue
  fi

  case "$title" in
    *Исполнитель*|*Чертежник* )
      if [ "$area" -gt "$best_area" ]; then
        best_area=$area
        main=$w
      fi
    ;;
  esac
done

# Fallback: choose biggest non-control window
if [ -z "$main" ]; then
  for w in $wins; do
    title=$(xdotool getwindowname $w 2>/dev/null || true)
    if is_control_title "$title"; then
      continue
    fi
    geom=$(xdotool getwindowgeometry --shell "$w" 2>/dev/null || true)
    W=$(echo "$geom" | grep '^WIDTH=' | cut -d= -f2 || echo 0)
    H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || echo 0)
    area=$((W*H))
    if [ "$area" -gt "$best_area" ]; then
      best_area=$area
      main=$w
    fi
  done
fi

log "chosen: main=$main(best_area=$best_area) start_btn=$start_btn step_btn=$step_btn"

# Focus main
xdotool windowactivate "$main" 2>/dev/null || true
xdotool windowraise "$main" 2>/dev/null || true
xdotool windowfocus "$main" 2>/dev/null || true
sleep 0.12
xdotool mousemove --window "$main" 140 120 click 1 2>/dev/null || true
sleep 0.12

# PRE (capture MAIN window, not root!)
PRE="{td}/pre.png"
shot_window "$main" "$PRE"
trim_png "$PRE"
log "PRE saved size=$(stat -c%s "$PRE" 2>/dev/null || echo 0)"

action_and_log() {{
  local name="$1"
  local cmd="$2"
  local post="{td}/post_${{name}}.png"

  if ! check_deadline_or_exit; then
    return 1
  fi

  log "ACTION: $name"
  # shellcheck disable=SC2086
  eval "$cmd" || true

  sleep 0.10
  shot_window "$main" "$post"
  trim_png "$post"

  if [ "$DIFF_LOG" = "1" ]; then
    local pct
    pct=$(diff_pct "$PRE" "$post")
    log "DIFF after $name: $pct % (thr={DIFF_THRESH_PCT})"
  fi

  return 0
}}

# ---- Interaction (порт “идеального” порядка) ----
# 1) Click Start button window-id if exists
if [ -n "$start_btn" ]; then
  action_and_log "click_start_btn" "xdotool windowactivate $start_btn 2>/dev/null; xdotool click --window $start_btn 1 2>/dev/null"
else
  # fallback: click bottom-left in main (как в идеале)
  geom=$(xdotool getwindowgeometry --shell "$main" 2>/dev/null || true)
  MW=$(echo "$geom" | grep '^WIDTH=' | cut -d= -f2 || echo 800)
  MH=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || echo 600)
  y1=$((MH-45)); y2=$((MH-55)); y3=$((MH-65)); y4=$((MH-75))
  action_and_log "click_start_area" "for x in 70 110 150; do for y in $y1 $y2 $y3 $y4; do xdotool mousemove --window $main $x $y click 1 2>/dev/null; sleep 0.06; done; done"
fi

sleep "$CAPTURE_DELAY"

# 2) Optional клавиши (как в идеале), но только если TF_SEND_KEYS=1
if [ "$SEND_KEYS" = "1" ]; then
  action_and_log "key_return" "xdotool key --window $main --clearmodifiers Return 2>/dev/null; xdotool key --window $main --clearmodifiers KP_Enter 2>/dev/null"
  action_and_log "key_space" "xdotool key --window $main --clearmodifiers space 2>/dev/null"
  sleep "$CAPTURE_DELAY"
fi

# 3) Step burst (мышкой) — если кнопка есть
if [ -n "$step_btn" ]; then
  if ! check_deadline_or_exit; then
    : # no-op
  else
    log "STEP_BURST: clicking step_btn=$step_btn repeat=$STEP_BURST delayMs=$STEP_DELAY_MS"
    # xdotool click supports --repeat/--delay (delay in ms)
    xdotool click --window "$step_btn" --repeat "$STEP_BURST" --delay "$STEP_DELAY_MS" 1 2>/dev/null || true

    # small post capture for diff log
    sleep 0.12
    POSTSTEP="{td}/post_step.png"
    shot_window "$main" "$POSTSTEP"
    trim_png "$POSTSTEP"
    if [ "$DIFF_LOG" = "1" ]; then
      pct=$(diff_pct "$PRE" "$POSTSTEP")
      log "DIFF after step_burst: $pct %"
    fi
  fi
else
  log "step button not found; skipping step burst"
fi

# Final capture (MAIN WINDOW)
FINAL="{out_png}"
shot_window "$main" "$FINAL"
trim_png "$FINAL"
log "FINAL out size=$(stat -c%s "$FINAL" 2>/dev/null || echo 0)"

# Cleanup
kill -TERM -$pgid >/dev/null 2>&1 || true
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
    send_keys = SEND_KEYS_DEFAULT not in ("0", "false", "False", "")
    diff_log = DIFF_LOG_DEFAULT not in ("0", "false", "False", "")

    step_burst = STEP_BURST_DEFAULT
    step_delay_ms = STEP_DELAY_MS_DEFAULT
    capture_delay = float(CAPTURE_DELAY_DEFAULT)

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s debug={debug} codeLen={len(src)}")
    _log(
        f"env: TF_SCREEN={SCREEN_W}x{SCREEN_H}x{SCREEN_D} TF_TRIM={TRIM_DEFAULT} "
        f"TF_SEND_KEYS={SEND_KEYS_DEFAULT} stepBurst={step_burst} stepDelayMs={step_delay_ms} diffLog={int(diff_log)}"
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

        # inner script budget немного меньше python-timeout
        inner_budget = max(5, run_timeout - 2)

        win_wait = min(WINDOW_WAIT_DEFAULT, max(6.0, remaining * 0.45))

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            win_wait=win_wait,
            do_trim=do_trim,
            send_keys=send_keys,
            step_burst=step_burst,
            step_delay_ms=step_delay_ms,
            capture_delay=capture_delay,
            diff_log=diff_log,
            run_budget_sec=inner_budget,
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

        _log(f"run: xvfb-run ... timeout={run_timeout}s (innerBudget={inner_budget}s)")
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

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(400, "failed to capture image (out.png not produced)\n\n" + _tail(log))

        total_sec = time.perf_counter() - t0
        png_bytes = out_png.read_bytes()
        _log(f"OK mode={mode} totalSec={total_sec:.3f}s -> returning png bytes size={len(png_bytes)}")
        return Response(content=png_bytes, media_type="image/png")
