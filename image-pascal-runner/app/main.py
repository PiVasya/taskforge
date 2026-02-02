import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge image pascal runner (MEGA DEBUG GraphABC/DrawMan)")


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    # Оставлено для обратной совместимости, но НЕ используется.
    # По требованию: МЕГА-ЛОГИ ВСЕГДА, без флагов в запросе.
    debug: bool = Field(False, description="(ignored) logs are always on")


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

# Default timing (can be overridden by env)
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
AFTER_ENTER_DELAY_DEFAULT = float(os.getenv("TF_AFTER_ENTER_DELAY", "10.0"))
WINDOW_WAIT_SECONDS_DEFAULT = float(os.getenv("TF_WINDOW_WAIT_SECONDS", "14.0"))

# If TF_TRIM=1 -> trim enabled. Default OFF (avoid empty image due to -trim).
TRIM_ENV = os.getenv("TF_TRIM")  # "1" / "0" / None

RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")


@app.get("/health")
def health():
    return {"ok": True}


def _tail(s: str, n: int = 9000) -> str:
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
    debug: bool,  # IMPORTANT: keep param (even if always True) to avoid TypeError
    run_timeout: int,
) -> str:
    # Keep delays inside the total budget.
    # Leave ~1s for screenshot + cleanup.
    after_enter_delay = min(AFTER_ENTER_DELAY_DEFAULT, max(0.5, run_timeout - 1.0))
    capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, run_timeout - 0.5))

    win_wait_seconds = max(3.0, min(WINDOW_WAIT_SECONDS_DEFAULT, run_timeout * 0.7))
    win_wait_seconds = min(win_wait_seconds, max(1.0, run_timeout - 1.0))

    # Trim policy:
    # Default OFF (trim sometimes causes "empty" image). Enable only via TF_TRIM=1.
    do_trim = TRIM_ENV == "1"

    return f"""#!/usr/bin/env bash
set -e

export LANG="{RUN_LANG}"
export LC_ALL="{RUN_LC_ALL}"

cd "{td}"

# redirect ALL runner output to runner.log AND stdout (container logs)
exec > >(tee -a "{td}/runner.log") 2>&1

now_s() {{ date +"%H:%M:%S"; }}
log() {{ echo "[runner] $(now_s) $*"; }}

# Logs ALWAYS ON by requirement
DEBUG=1
NEEDS_ENTER={'1' if needs_enter else '0'}
DO_TRIM={'1' if do_trim else '0'}

WIN_WAIT="{win_wait_seconds}"
AFTER_ENTER="{after_enter_delay}"
CAPTURE_DELAY="{capture_delay}"

have() {{ command -v "$1" >/dev/null 2>&1; }}

dbg() {{ log "DBG: $*"; }}

log "===== runner start ====="
log "pid search needs_enter=$NEEDS_ENTER debug=$DEBUG do_trim=$DO_TRIM"
log "timeouts: WIN_WAIT=$WIN_WAIT AFTER_ENTER=$AFTER_ENTER CAPTURE_DELAY=$CAPTURE_DELAY"
log "tools: xdotool=$(have xdotool && echo yes || echo no) import=$(have import && echo yes || echo no) convert=$(have convert && echo yes || echo no) identify=$(have identify && echo yes || echo no) sha256sum=$(have sha256sum && echo yes || echo no)"

# start program
log "starting: mono {exe_path}"
mono "{exe_path}" > program.log 2>&1 &
pid=$!
log "mono pid=$pid"

# GUI warm-up
sleep 0.7

# helpers
stat_sz() {{
  local f="$1"
  if [ -f "$f" ]; then
    stat -c%s "$f" 2>/dev/null || echo "?"
  else
    echo "missing"
  fi
}}

hash_file() {{
  local f="$1"
  if [ -f "$f" ] && have sha256sum; then
    sha256sum "$f" 2>/dev/null | awk '{{print $1}}'
  else
    echo ""
  fi
}}

id_file() {{
  local f="$1"
  if [ -f "$f" ] && have identify; then
    identify -format "%m %wx%h" "$f" 2>/dev/null || echo "identify_failed"
  else
    echo ""
  fi
}}

info_png() {{
  local f="$1"
  local sz
  sz=$(stat_sz "$f")
  local id
  id=$(id_file "$f")
  local hs
  hs=$(hash_file "$f")
  dbg "PNG: file=$f size=$sz identify='$id' sha256='$hs'"
}}

shot_root() {{
  local f="$1"
  dbg "screenshot root -> $f"
  if have import; then
    import -window root "$f" >/dev/null 2>&1 || true
  else
    dbg "import missing; cannot screenshot"
  fi
  info_png "$f"
}}

maybe_trim() {{
  local f="$1"
  if [ "$DO_TRIM" != "1" ]; then
    dbg "trim skipped (DO_TRIM=$DO_TRIM) for $f"
    return
  fi
  if [ -f "$f" ] && have convert; then
    dbg "trim -> $f"
    convert "$f" -trim +repage "$f" >/dev/null 2>&1 || true
  fi
  info_png "$f"
}}

# Dump top visible windows (diagnostic)
dump_visible_windows() {{
  if ! have xdotool; then return; fi
  log "visible windows (last 30):"
  local c=0
  for w in $(xdotool search --onlyvisible --name ".*" 2>/dev/null | tail -n 30 || true); do
    local title
    title=$(xdotool getwindowname "$w" 2>/dev/null || true)
    log "  $w -> $title"
    c=$((c+1))
  done
  if [ "$c" -eq 0 ]; then
    log "  (none found)"
  fi
}}

wins=""
win=""

if [ "$NEEDS_ENTER" = "1" ] && have xdotool; then
  log "needs_enter=1; waiting for windows by pid=$pid up to $WIN_WAIT s"

  WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
  if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi

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
    log "candidate windows:"
    for w in $wins; do
      title=$(xdotool getwindowname "$w" 2>/dev/null || true)
      log "  $w -> $title"
    done

    # Choose best window
    for w in $wins; do
      title=$(xdotool getwindowname "$w" 2>/dev/null || true)
      case "$title" in *Справка* ) continue;; esac
      case "$title" in *Чертежник* ) win=$w; break;; esac
    done

    if [ -z "$win" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname "$w" 2>/dev/null || true)
        case "$title" in *Справка* ) continue;; esac
        case "$title" in *Поле* ) win=$w; break;; esac
      done
    fi

    if [ -z "$win" ]; then
      for w in $wins; do
        title=$(xdotool getwindowname "$w" 2>/dev/null || true)
        if [ -n "$title" ]; then win=$w; break; fi
      done
    fi

    if [ -z "$win" ]; then
      win=$(echo "$wins" | head -n 1)
    fi
  else
    log "no windows found within wait"
    dump_visible_windows
  fi
fi

focus_and_click() {{
  local w="$1"
  dbg "focus_and_click window=$w"
  xdotool windowactivate "$w" 2>/dev/null || true
  xdotool windowraise "$w" 2>/dev/null || true
  xdotool windowfocus "$w" 2>/dev/null || true
  sleep 0.12
  xdotool mousemove --window "$w" 140 120 click 1 2>/dev/null || true
  sleep 0.12
}}

send_enters() {{
  local w="$1"
  log "sending Enter strategies to window=$w"
  xdotool key --window "$w" --clearmodifiers Return 2>/dev/null || true
  xdotool key --window "$w" --clearmodifiers KP_Enter 2>/dev/null || true
  xdotool key --window "$w" --clearmodifiers ISO_Enter 2>/dev/null || true

  xdotool keydown --window "$w" Return 2>/dev/null || true
  xdotool keyup --window "$w" Return 2>/dev/null || true

  xdotool keydown --window "$w" KP_Enter 2>/dev/null || true
  xdotool keyup --window "$w" KP_Enter 2>/dev/null || true

  # also send to active
  xdotool key --clearmodifiers Return 2>/dev/null || true
  xdotool key --clearmodifiers KP_Enter 2>/dev/null || true

  # newline type
  xdotool type --window "$w" --clearmodifiers $'\\n' 2>/dev/null || true
}}

click_start_area() {{
  local w="$1"
  local geom H y1 y2 y3 y4
  geom=$(xdotool getwindowgeometry --shell "$w" 2>/dev/null || true)
  dbg "geom: $geom"
  H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)
  if [ -n "$H" ]; then
    y1=$((H-45)); y2=$((H-55)); y3=$((H-65)); y4=$((H-75))
  else
    y1=700; y2=690; y3=680; y4=670
  fi
  log "clicking start-area guesses (H=${{H:-?}}): y=$y1,$y2,$y3,$y4"
  xdotool mousemove --window "$w" 70 "$y1" click 1 2>/dev/null || true
  xdotool mousemove --window "$w" 70 "$y2" click 1 2>/dev/null || true
  xdotool mousemove --window "$w" 70 "$y3" click 1 2>/dev/null || true
  xdotool mousemove --window "$w" 70 "$y4" click 1 2>/dev/null || true
}}

# Always take a PRE screenshot to see if root is capturing anything at all
PRE="{td}/pre.png"
log "taking PRE screenshot"
shot_root "$PRE"
maybe_trim "$PRE"

if [ "$NEEDS_ENTER" = "1" ]; then
  if have xdotool && [ -n "$win" ]; then
    log "chosen window=$win"
    focus_and_click "$win"
    send_enters "$win"
    click_start_area "$win"
    # repeat enter after click
    xdotool key --window "$win" --clearmodifiers Return 2>/dev/null || true
    xdotool key --window "$win" --clearmodifiers KP_Enter 2>/dev/null || true

    log "waiting after enter: $AFTER_ENTER s"
    sleep "$AFTER_ENTER"
  else
    log "DrawMan mode BUT window not found or xdotool missing. win='$win'"
    dump_visible_windows
    log "sleep CAPTURE_DELAY=$CAPTURE_DELAY"
    sleep "$CAPTURE_DELAY"
  fi
else
  log "non-DrawMan mode; sleep CAPTURE_DELAY=$CAPTURE_DELAY"
  sleep "$CAPTURE_DELAY"
fi

POST="{td}/post.png"
log "taking POST screenshot"
shot_root "$POST"
maybe_trim "$POST"

log "taking FINAL screenshot out.png"
shot_root "{out_png}"
maybe_trim "{out_png}"

log "listing artifacts:"
ls -la "{td}" 2>/dev/null || true

log "tail program.log (last 120 lines):"
tail -n 120 program.log 2>/dev/null || true

# Cleanup (do not fail runner if cleanup fails)
log "cleanup: kill pid=$pid"
kill $pid >/dev/null 2>&1 || true
wait $pid >/dev/null 2>&1 || true

log "===== runner end ====="
exit 0
"""


@app.post("/render")
def render(req: RenderRequest):
    t0 = time.perf_counter()
    src = req.source or ""
    mode = _detect_mode(src)
    needs_enter = mode == "DrawMan"

    # По требованию: МЕГА-ЛОГИ ВСЕГДА, без флагов в запросе.
    debug = True

    run_timeout = int(req.timeout_seconds or 1)
    if run_timeout < 1:
        run_timeout = 1

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={run_timeout}s debug={debug} codeLen={len(src)}")

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        src_path.write_text(src, encoding="utf-8")

        # Compile (separate timeout)
        try:
            _log("compile: mono pabcnetc ...")
            cp = subprocess.run(
                ["mono", PABCNETC, str(src_path)],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=60,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "compile timeout")

        _log(f"compile done exitCode={cp.returncode}")
        if cp.stdout:
            _log("compile output (tail):\n" + _tail(cp.stdout))

        if cp.returncode != 0:
            _log("COMPILE FAILED, output (tail):\n" + _tail(cp.stdout))
            raise HTTPException(400, f"compile failed:\n{_tail(cp.stdout)}")

        exe_path = td_path / "main.exe"
        if not exe_path.exists():
            exes = list(td_path.glob("*.exe"))
            if exes:
                exe_path = exes[0]
            else:
                raise HTTPException(500, "compile succeeded but no .exe produced")

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,  # <-- now accepted (no TypeError)
            run_timeout=run_timeout,
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

        try:
            _log(f"run: xvfb-run ... timeout={run_timeout}s")
            rp = subprocess.run(
                cmd,
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=run_timeout,
            )
        except subprocess.TimeoutExpired:
            _log("RUN TIMEOUT")
            log_path = td_path / "program.log"
            log = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
            raise HTTPException(504, "run timeout\n\nprogram.log:\n" + _tail(log))

        if rp.stdout:
            _log("run.sh stdout (tail):\n" + _tail(rp.stdout))

        _log(f"run done exitCode={rp.returncode}")

        # Always print program.log + runner.log tails (logs are ALWAYS ON)
        pl = td_path / "program.log"
        prog = pl.read_text(encoding="utf-8", errors="replace") if pl.exists() else ""
        _log("program.log (tail):\n" + _tail(prog))

        rl = td_path / "runner.log"
        runner_log = rl.read_text(encoding="utf-8", errors="replace") if rl.exists() else ""
        _log("runner.log (tail):\n" + _tail(runner_log))

        if rp.returncode != 0:
            raise HTTPException(400, f"runtime error:\n{_tail(rp.stdout)}\n\nprogram.log:\n{_tail(prog)}")

        if not out_png.exists() or out_png.stat().st_size == 0:
            _log("OUT.PNG missing/empty")
            raise HTTPException(
                400,
                "failed to capture image (out.png not produced).\n\n"
                f"runner.log:\n{_tail(runner_log)}\n\n"
                f"program.log:\n{_tail(prog)}",
            )

        total = time.perf_counter() - t0
        _log(f"OK mode={mode} totalSec={total:.3f}s -> returning png bytes size={out_png.stat().st_size}")

        return Response(content=out_png.read_bytes(), media_type="image/png")
