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


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

WINDOW_WAIT_DEFAULT = float(os.getenv("TF_WINDOW_WAIT", "14.0"))

# минимум ждать после нажатия "Пуск" до начала ожидания завершения
AFTER_START_DEFAULT = float(os.getenv("TF_AFTER_START", "2.0"))
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))

TRIM_DEFAULT = os.getenv("TF_TRIM", "0").strip()  # 1/0

# КЛЮЧЕВОЕ: клавиши по умолчанию ВЫКЛ (mono/WinForms может падать)
SEND_KEYS_DEFAULT = os.getenv("TF_SEND_KEYS", "0").strip()  # 1/0


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
    after_start: float,
    capture_delay: float,
    do_trim: bool,
    send_keys: bool,
    wait_finish_max: float,
) -> str:
    return f"""#!/usr/bin/env bash
set -euo pipefail

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

WIN_WAIT="{win_wait}"
AFTER_START="{after_start}"
CAPTURE_DELAY="{capture_delay}"
WAIT_FINISH_MAX="{wait_finish_max}"

WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1); if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi
WAIT_FINISH_MAX_INT=$(echo "$WAIT_FINISH_MAX" | cut -d. -f1); if [ -z "$WAIT_FINISH_MAX_INT" ]; then WAIT_FINISH_MAX_INT=15; fi

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

focus_and_click() {{
  local w="$1"
  xdotool windowactivate "$w" 2>/dev/null || true
  xdotool windowraise "$w" 2>/dev/null || true
  xdotool windowfocus "$w" 2>/dev/null || true
  sleep 0.12
  xdotool mousemove --window "$w" 140 120 click 1 2>/dev/null || true
  sleep 0.12
}}

send_enter() {{
  local w="$1"
  if [ "$SEND_KEYS" != "1" ]; then
    return
  fi
  xdotool key --window "$w" --clearmodifiers Return 2>/dev/null || true
  xdotool key --window "$w" --clearmodifiers KP_Enter 2>/dev/null || true
}}

log "===== runner start ====="
log "needs_enter=$NEEDS_ENTER do_trim=$DO_TRIM send_keys=$SEND_KEYS"
log "timeouts: WIN_WAIT=$WIN_WAIT AFTER_START=$AFTER_START CAPTURE_DELAY=$CAPTURE_DELAY WAIT_FINISH_MAX=$WAIT_FINISH_MAX"
log "tools: xdotool=$(have xdotool && echo yes || echo no) import=$(have import && echo yes || echo no) convert=$(have convert && echo yes || echo no)"

mono "{exe_path}" > program.log 2>&1 &
pid=$!
log "mono pid=$pid"

sleep 0.7

PRE="{td}/pre.png"
log "taking PRE screenshot"
shot_root "$PRE"
trim_png "$PRE"
log "pre size=$(stat -c%s "$PRE" 2>/dev/null || echo 0)"

# Non-DrawMan
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
win=""

log "DrawMan: waiting windows by pid=$pid up to $WIN_WAIT s"
end=$(( $(date +%s) + WIN_WAIT_INT ))
while [ $(date +%s) -lt $end ]; do
  wins=$(xdotool search --onlyvisible --pid $pid 2>/dev/null || true)
  if [ -n "$wins" ]; then break; fi
  sleep 0.1
done

if [ -z "$wins" ]; then
  log "DrawMan: no windows found. capture anyway."
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

# Choose main window (Чертежник)
for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)
  case "$title" in *Справка* ) continue;; esac
  case "$title" in *Чертежник* ) win=$w; break;; esac
done
if [ -z "$win" ]; then
  win=$(echo "$wins" | head -n 1)
fi

log "DrawMan: chosen window=$win"
focus_and_click "$win"

geom=$(xdotool getwindowgeometry --shell "$win" 2>/dev/null || true)
W=$(echo "$geom" | grep '^WIDTH=' | cut -d= -f2 || true)
H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)
if [ -z "$W" ]; then W="0"; fi
if [ -z "$H" ]; then H="0"; fi

# 1) SPEED: тянем ползунок скорости вправо (самый надежный способ)
if [ "$W" -gt 0 ] && [ "$H" -gt 0 ]; then
  sy=$((H-38))
  x_from=$((W-160))
  x_to=$((W-25))
  log "speed drag: ($x_from,$sy) -> ($x_to,$sy) (W=$W H=$H)"
  xdotool mousemove --window "$win" "$x_from" "$sy" mousedown 1 2>/dev/null || true
  sleep 0.10
  xdotool mousemove --window "$win" "$x_to" "$sy" 2>/dev/null || true
  sleep 0.10
  xdotool mouseup 1 2>/dev/null || true
  sleep 0.15
fi

# 2) START: клики по координатам зоны кнопки "Пуск"
if [ "$H" -gt 0 ]; then
  y1=$((H-45)); y2=$((H-55)); y3=$((H-65)); y4=$((H-75))
else
  y1=418; y2=408; y3=398; y4=388
fi

log "start clicks in main window: x=70,110,150 y=$y1,$y2,$y3,$y4"
for x in 70 110 150; do
  for y in $y1 $y2 $y3 $y4; do
    xdotool mousemove --window "$win" "$x" "$y" click 1 2>/dev/null || true
    sleep 0.08
  done
done

# 3) запасной вариант: если нашли отдельное окно "Пуск (Enter)" — попробуем и его
start_btn=""
for w in $wins; do
  title=$(xdotool getwindowname $w 2>/dev/null || true)
  case "$title" in *"Пуск (Enter)"* ) start_btn=$w;; esac
done
if [ -n "$start_btn" ]; then
  log "extra: click Start button by window-id: $start_btn"
  xdotool windowactivate "$start_btn" 2>/dev/null || true
  xdotool click --window "$start_btn" 1 2>/dev/null || true
  sleep 0.15
fi

# 4) (опасно) Enter — только если TF_SEND_KEYS=1
send_enter "$win"

# Даем чуть времени стартануть
log "sleep AFTER_START=$AFTER_START s"
sleep "$AFTER_START"

# ГЛАВНОЕ: ЖДЕМ ЗАВЕРШЕНИЯ ПРОЦЕССА (иначе мы его убиваем слишком рано)
log "waiting for process to finish up to $WAIT_FINISH_MAX s"
end2=$(( $(date +%s) + WAIT_FINISH_MAX_INT ))
while kill -0 $pid >/dev/null 2>&1; do
  if [ $(date +%s) -ge $end2 ]; then
    log "finish wait max reached"
    break
  fi
  sleep 0.25
done

# еще небольшой буфер перед скрином
sleep "$CAPTURE_DELAY"

log "taking FINAL screenshot out.png"
shot_root "{out_png}"
trim_png "{out_png}"
log "out size=$(stat -c%s "{out_png}" 2>/dev/null || echo 0)"

# Если out явно плохой — вернём pre
outSize=$(stat -c%s "{out_png}" 2>/dev/null || echo 0)
preSize=$(stat -c%s "$PRE" 2>/dev/null || echo 0)
if [ "$outSize" -lt 1000 ] && [ "$preSize" -gt "$outSize" ]; then
  log "out.png looks bad (size=$outSize). using pre.png (size=$preSize)"
  cp "$PRE" "{out_png}" >/dev/null 2>&1 || true
fi

log "cleanup: stopping process if still alive"
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

        win_wait = min(WINDOW_WAIT_DEFAULT, max(10.0, remaining * 0.5))
        after_start = float(AFTER_START_DEFAULT)
        capture_delay = float(CAPTURE_DELAY_DEFAULT)

        # Сколько максимум ждать завершения после старта (чтобы замок успел)
        # берем почти весь run_timeout
        wait_finish_max = max(4.0, float(run_timeout) - 2.0)

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            debug=debug,
            win_wait=win_wait,
            after_start=after_start,
            capture_delay=capture_delay,
            do_trim=do_trim,
            send_keys=send_keys,
            wait_finish_max=wait_finish_max,
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
        png_bytes = out_png.read_bytes()
        _log(f"OK mode={mode} totalSec={total_sec:.3f}s -> returning png bytes size={len(png_bytes)}")
        return Response(content=png_bytes, media_type="image/png")
