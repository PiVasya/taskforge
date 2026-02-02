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

# Headless screen size (root screenshot will have this size)
SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

# Locale for tools (avoid en_US.UTF-8 if locales are not generated in container)
RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

# Optional: trim output image (ImageMagick convert -trim)
DO_TRIM_DEFAULT = os.getenv("TF_TRIM", "0").strip() in ("1", "true", "yes", "on")

# DrawMan start interaction tuning
WINDOW_WAIT_DEFAULT = float(os.getenv("TF_WINDOW_WAIT", "14.0"))  # seconds
# Click guesses in start-area near bottom-left
CLICK_XS = os.getenv("TF_CLICK_XS", "70,110,150")  # comma list
CLICK_Y_OFFSETS = os.getenv("TF_CLICK_Y_OFFSETS", "45,55,65,75")  # from bottom: H-offset
# Sending keys sometimes crashes Mono/WinForms in Xvfb on some builds. Default OFF.
SEND_KEYS_DEFAULT = os.getenv("TF_SEND_KEYS", "0").strip() in ("1", "true", "yes", "on")

# "Wait until drawing finished" tuning (this is the important fix)
# We consider screen stable if diffPct <= STABLE_PCT for STABLE_NEED consecutive checks.
STABLE_PCT_DEFAULT = float(os.getenv("TF_STABLE_PCT", "0.005"))  # percent, e.g. 0.005%
STABLE_NEED_DEFAULT = int(os.getenv("TF_STABLE_NEED", "3"))
CHECK_SLEEP_DEFAULT = float(os.getenv("TF_CHECK_SLEEP", "0.7"))  # seconds
# Hard cap for stabilization wait, if not set computed from timeout
MAX_STABLE_WAIT_DEFAULT = float(os.getenv("TF_MAX_STABLE_WAIT", "0"))  # 0 = auto


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Verbose runner logs + keep pre/post checks")


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
    total_timeout: int,
    do_trim: bool,
    send_keys: bool,
    stable_pct: float,
    stable_need: int,
    check_sleep: float,
    max_stable_wait: float,
) -> str:
    """
    Bash script runs inside Xvfb.
    - starts mono exe
    - if DrawMan: finds window, focuses, clicks start area (keys optional)
    - waits until screen stabilizes (diff between consecutive screenshots becomes tiny)
    - captures final out.png (root window)
    """

    click_xs = [x.strip() for x in CLICK_XS.split(",") if x.strip()]
    click_y_offsets = [y.strip() for y in CLICK_Y_OFFSETS.split(",") if y.strip()]

    # Convert lists to bash arrays literals
    bx = " ".join(click_xs) if click_xs else "70 110 150"
    byoff = " ".join(click_y_offsets) if click_y_offsets else "45 55 65 75"

    # max wait auto: keep 2 seconds for final capture+cleanup, minimum 3
    # also allow explicit TF_MAX_STABLE_WAIT > 0
    # (we pass total_timeout from python, so bash can compute precisely)
    max_wait_expr = (
        f"MAX_WAIT={int(max_stable_wait)}"
        if max_stable_wait and max_stable_wait > 0
        else f"MAX_WAIT=$(( {total_timeout} - 2 ))\nif [ \"$MAX_WAIT\" -lt 3 ]; then MAX_WAIT=3; fi"
    )

    return f"""#!/usr/bin/env bash
set -euo pipefail

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

echo "[runner] {time.strftime('%H:%M:%S')} ===== runner start ====="
echo "[runner] {time.strftime('%H:%M:%S')} needs_enter={'1' if needs_enter else '0'} do_trim={'1' if do_trim else '0'} send_keys={'1' if send_keys else '0'}"
echo "[runner] {time.strftime('%H:%M:%S')} timeouts: WIN_WAIT={win_wait} TOTAL_TIMEOUT={total_timeout}"
echo "[runner] {time.strftime('%H:%M:%S')} stable: pct<={stable_pct}% need={stable_need} check_sleep={check_sleep}s max_stable_wait={max_stable_wait if max_stable_wait else 0}"

have() {{ command -v "$1" >/dev/null 2>&1; }}
now_s() {{ date +"%H:%M:%S"; }}
log() {{ echo "[runner] $(now_s) $*"; }}

# Start program
log "starting: mono {exe_path}"
mono "{exe_path}" > program.log 2>&1 &
pid=$!
log "mono pid=$pid"

# Small GUI warm-up
sleep 0.7

DO_TRIM={'1' if do_trim else '0'}
DEBUG={'1' if debug else '0'}
SEND_KEYS={'1' if send_keys else '0'}

WIN_WAIT="{win_wait}"
WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi

STABLE_PCT="{stable_pct}"
STABLE_NEED="{stable_need}"
CHECK_SLEEP="{check_sleep}"

{max_wait_expr}

shot_root() {{
  local file="$1"
  if have import; then
    import -window root "$file" >/dev/null 2>&1 || true
  fi
}}

maybe_trim() {{
  local file="$1"
  if [ "$DO_TRIM" = "1" ] && have convert; then
    convert "$file" -trim +repage "$file" >/dev/null 2>&1 || true
  fi
}}

diff_pct() {{
  local a="$1"
  local b="$2"
  if ! have compare || ! have identify; then
    echo "100"
    return
  fi
  local w h total ae
  w=$(identify -format "%w" "$a" 2>/dev/null || echo "0")
  h=$(identify -format "%h" "$a" 2>/dev/null || echo "0")
  total=$(( w * h ))
  if [ "$total" -le 0 ]; then
    echo "100"
    return
  fi
  ae=$(compare -metric AE "$a" "$b" null: 2>&1 || true)
  ae=$(echo "$ae" | tr -cd "0-9")
  if [ -z "$ae" ]; then ae="0"; fi
  awk -v ae="$ae" -v total="$total" 'BEGIN {{ printf "%.6f", (ae/total)*100.0 }}'
}}

screenshot_info() {{
  local file="$1"
  if have identify; then
    local info
    info=$(identify -format "PNG %wx%h" "$file" 2>/dev/null || true)
    local size
    size=$(stat -c%s "$file" 2>/dev/null || echo 0)
    log "screenshot: $file size=$size identify='$info'"
  fi
}}

# Take PRE screenshot early (helps debug)
PRE="{td}/pre.png"
log "taking PRE screenshot"
shot_root "$PRE"
maybe_trim "$PRE"
screenshot_info "$PRE"

# If DrawMan: find window and click "Пуск"
win=""
wins=""
if [ "{'1' if needs_enter else '0'}" = "1" ] && have xdotool; then
  log "DrawMan: waiting windows by pid=$pid up to ${WIN_WAIT_INT}.0 s"

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

    # prefer main window with "Чертежник" (skip help)
    for w in $wins; do
      title=$(xdotool getwindowname $w 2>/dev/null || true)
      case "$title" in *Справка* ) continue;; esac
      case "$title" in *Чертежник* ) win=$w; break;; esac
    done
    # fallback: any non-help window
    if [ -z "$win" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname $w 2>/dev/null || true)
        case "$title" in *Справка* ) continue;; esac
        win=$w
        break
      done
    fi
  fi

  if [ -n "$win" ]; then
    log "DrawMan: chosen window=$win"

    # focus it
    xdotool windowactivate "$win" 2>/dev/null || true
    xdotool windowraise "$win" 2>/dev/null || true
    xdotool windowfocus "$win" 2>/dev/null || true
    sleep 0.12

    # click inside to ensure focus
    xdotool mousemove --window "$win" 140 120 click 1 2>/dev/null || true
    sleep 0.12

    geom=$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)
    H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)
    if [ -z "$H" ]; then H="0"; fi

    # Start-area clicks near bottom-left (this is the most stable method)
    if [ "$H" -gt 0 ]; then
      log "clicking start button guesses (H=$H): xs={bx} offsets_from_bottom={byoff}"
      for x in {bx}; do
        for off in {byoff}; do
          y=$((H - off))
          xdotool mousemove --window "$win" $x $y click 1 2>/dev/null || true
          sleep 0.18
        done
      done
    else
      log "no geometry; clicking some defaults"
      for x in {bx}; do
        for y in 418 408 398 388; do
          xdotool mousemove --window "$win" $x $y click 1 2>/dev/null || true
          sleep 0.18
        done
      done
    fi

    # Optional key presses (OFF by default because some Mono builds segfault here)
    if [ "$SEND_KEYS" = "1" ]; then
      log "sending Enter keys (SEND_KEYS=1)"
      xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
      xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true
      xdotool key --clearmodifiers Return 2>/dev/null || true
      sleep 0.25
    fi

  else
    log "DrawMan: window not found (will just stabilize/capture root)"
  fi
fi

# ---- Stabilization wait (THE FIX) ----
log "waiting until image stabilizes: max=${{MAX_WAIT}}s stable_pct=${{STABLE_PCT}}% need=${{STABLE_NEED}} interval=${{CHECK_SLEEP}}s"

prev="{td}/wait_prev.png"
cur="{td}/wait_cur.png"
shot_root "$prev"
maybe_trim "$prev"

stable=0
end=$(( $(date +%s) + MAX_WAIT ))

while [ $(date +%s) -lt $end ]; do
  sleep "$CHECK_SLEEP"
  shot_root "$cur"
  maybe_trim "$cur"

  pct=$(diff_pct "$prev" "$cur")
  pass=$(awk -v p="$pct" -v t="$STABLE_PCT" 'BEGIN {{ if (p+0 <= t+0) print 1; else print 0; }}')

  log "stabilize check: diffPct=$pct pass=$pass stableCount=$stable"

  if [ "$pass" = "1" ]; then
    stable=$((stable+1))
    if [ "$stable" -ge "$STABLE_NEED" ]; then
      log "stabilized ✅"
      break
    fi
  else
    stable=0
  fi

  cp "$cur" "$prev" >/dev/null 2>&1 || true
done

# POST (for debug)
POST="{td}/post.png"
log "taking POST screenshot"
shot_root "$POST"
maybe_trim "$POST"
screenshot_info "$POST"

# Final capture
log "taking FINAL screenshot out.png"
shot_root "{out_png}"
maybe_trim "{out_png}"
screenshot_info "{out_png}"

log "cleanup: kill pid=$pid"
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

    do_trim = DO_TRIM_DEFAULT
    send_keys = SEND_KEYS_DEFAULT

    stable_pct = STABLE_PCT_DEFAULT
    stable_need = STABLE_NEED_DEFAULT
    check_sleep = CHECK_SLEEP_DEFAULT
    max_stable_wait = MAX_STABLE_WAIT_DEFAULT

    _log(
        f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s "
        f"debug={debug} codeLen={len(src)}"
    )
    _log(
        f"env: PABCNETC={PABCNETC} TF_SCREEN={SCREEN_W}x{SCREEN_H}x{SCREEN_D} "
        f"TF_TRIM={int(do_trim)} TF_SEND_KEYS={int(send_keys)} "
        f"stable(pct={stable_pct},need={stable_need},sleep={check_sleep},max={max_stable_wait})"
    )

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        # Write source
        src_bytes = src.encode("utf-8", errors="replace")
        src_path.write_bytes(src_bytes)
        _log(f"write source: {src_path} bytes={len(src_bytes)}")

        # Compile
        t_compile0 = time.perf_counter()
        try:
            # compile may be slow; keep decent chunk of timeout
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
        _log(f"compile done exitCode={cp.returncode}")
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

        # Window wait: keep it bounded; can be at most default or 60% of remaining
        remaining = max(5.0, float(total_timeout) - compile_sec - 1.0)
        win_wait = min(WINDOW_WAIT_DEFAULT, max(6.0, remaining * 0.6))

        # Run timeout: keep full total_timeout for xvfb-run (it includes stabilization)
        run_timeout = total_timeout

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            win_wait=win_wait,
            total_timeout=run_timeout,
            do_trim=do_trim,
            send_keys=send_keys,
            stable_pct=stable_pct,
            stable_need=stable_need,
            check_sleep=check_sleep,
            max_stable_wait=max_stable_wait,
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
            raise HTTPException(400, f"runtime error:\n{_tail(rp.stdout)}\n\nprogram.log:\n{_tail(log)}")

        if not out_png.exists() or out_png.stat().st_size == 0:
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(400, "failed to capture image (out.png not produced)\n\n" + _tail(log))

        total_sec = time.perf_counter() - t0
        _log(f"OK mode={mode} totalSec={total_sec:.3f}s -> returning png bytes size={out_png.stat().st_size}")
        return Response(content=out_png.read_bytes(), media_type="image/png")
