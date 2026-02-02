import os
import subprocess
import tempfile
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge image pascal runner (ALWAYS LOGS, SAFE DrawMan)")

class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    # Оставлено только ради совместимости со старыми клиентами.
    # По требованию: логи ВСЕГДА, без флагов.
    debug: bool = Field(False, description="(ignored) logs are always on")


PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

# Тайминги (можно подкрутить env, но дефолты рабочие)
CAPTURE_DELAY_DEFAULT = float(os.getenv("TF_CAPTURE_DELAY", "0.8"))
AFTER_START_DELAY_DEFAULT = float(os.getenv("TF_AFTER_START_DELAY", "10.0"))
WINDOW_WAIT_SECONDS_DEFAULT = float(os.getenv("TF_WINDOW_WAIT_SECONDS", "14.0"))

# TRIM: по умолчанию ОТКЛЮЧЕН (иногда даёт “пустоту”)
# TF_TRIM=1 -> включить всегда, TF_TRIM=0 -> выключить всегда
TRIM_ENV = os.getenv("TF_TRIM")  # "1" / "0" / None


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
    run_timeout: int,
) -> str:
    # Подстраиваем задержки под общий таймаут
    capture_delay = min(CAPTURE_DELAY_DEFAULT, max(0.2, run_timeout - 0.5))
    after_start_delay = min(AFTER_START_DELAY_DEFAULT, max(0.5, run_timeout - 1.0))
    win_wait_seconds = max(3.0, min(WINDOW_WAIT_SECONDS_DEFAULT, run_timeout * 0.7))
    win_wait_seconds = min(win_wait_seconds, max(1.0, run_timeout - 1.0))

    do_trim = (TRIM_ENV == "1")

    # ВАЖНО:
    # - никаких xdotool type / Enter-ключей -> именно они чаще валят Mono (у тебя SIGSEGV в X11Keyboard)
    # - стартуем DrawMan ТОЛЬКО кликом по кнопке "Пуск (Enter)" (несколько координат)
    # - если mono умер -> выходим с ошибкой, чтобы API не отдавал 200 + чёрный PNG

    return f"""#!/usr/bin/env bash
set -euo pipefail

cd "{td}"

# ВСЕ логи в runner.log и stdout контейнера
exec > >(tee -a "{td}/runner.log") 2>&1

now_s() {{ date +"%H:%M:%S"; }}
log() {{ echo "[runner] $(now_s) $*"; }}

have() {{ command -v "$1" >/dev/null 2>&1; }}

# Убираем XIM/IME, чтобы снизить шанс креша по клавиатуре (мы и так не шлём клавиши)
export LANG=C
export LC_ALL=C
export XMODIFIERS=@im=none
export GTK_IM_MODULE=none
export QT_IM_MODULE=simple

NEEDS_ENTER={'1' if needs_enter else '0'}
DO_TRIM={'1' if do_trim else '0'}

WIN_WAIT="{win_wait_seconds}"
AFTER_START="{after_start_delay}"
CAPTURE_DELAY="{capture_delay}"

log "===== runner start ====="
log "needs_enter=$NEEDS_ENTER do_trim=$DO_TRIM"
log "timeouts: WIN_WAIT=$WIN_WAIT AFTER_START=$AFTER_START CAPTURE_DELAY=$CAPTURE_DELAY"
log "tools: xdotool=$(have xdotool && echo yes || echo no) import=$(have import && echo yes || echo no) convert=$(have convert && echo yes || echo no) identify=$(have identify && echo yes || echo no)"

log "starting: mono {exe_path}"
mono "{exe_path}" > program.log 2>&1 &
pid=$!
log "mono pid=$pid"

sleep 0.7

is_alive() {{
  kill -0 "$pid" >/dev/null 2>&1
}}

stat_sz() {{
  local f="$1"
  if [ -f "$f" ]; then stat -c%s "$f" 2>/dev/null || echo "?"; else echo "missing"; fi
}}

id_file() {{
  local f="$1"
  if [ -f "$f" ] && have identify; then identify -format "%m %wx%h" "$f" 2>/dev/null || echo "identify_failed"; else echo ""; fi
}}

shot_root() {{
  local f="$1"
  if have import; then
    import -window root "$f" >/dev/null 2>&1 || true
  fi
  log "screenshot: $f size=$(stat_sz "$f") identify='$(id_file "$f")'"
}}

maybe_trim() {{
  local f="$1"
  if [ "$DO_TRIM" = "1" ] && [ -f "$f" ] && have convert; then
    convert "$f" -trim +repage "$f" >/dev/null 2>&1 || true
    log "trimmed: $f size=$(stat_sz "$f") identify='$(id_file "$f")'"
  fi
}}

dump_visible_windows() {{
  if ! have xdotool; then return; fi
  log "visible windows (last 40):"
  local c=0
  for w in $(xdotool search --onlyvisible --name ".*" 2>/dev/null | tail -n 40 || true); do
    local title
    title=$(xdotool getwindowname "$w" 2>/dev/null || true)
    log "  $w -> $title"
    c=$((c+1))
  done
  if [ "$c" -eq 0 ]; then log "  (none found)"; fi
}}

# PRE (для диагностики)
PRE="{td}/pre.png"
log "taking PRE screenshot"
shot_root "$PRE"
maybe_trim "$PRE"

win=""

if [ "$NEEDS_ENTER" = "1" ] && have xdotool; then
  log "DrawMan: waiting windows by pid=$pid up to $WIN_WAIT s"

  WIN_WAIT_INT=$(echo "$WIN_WAIT" | cut -d. -f1)
  if [ -z "$WIN_WAIT_INT" ]; then WIN_WAIT_INT=14; fi

  end=$(( $(date +%s) + WIN_WAIT_INT ))
  wins=""
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
      title=$(xdotool getwindowname "$w" 2>/dev/null || true)
      log "  $w -> $title"
    done

    # Prefer "Исполнитель Чертежник"
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
      win=$(echo "$wins" | head -n 1)
    fi
  else
    log "no windows found"
    dump_visible_windows
  fi
fi

focus_and_click() {{
  local w="$1"
  xdotool windowactivate "$w" 2>/dev/null || true
  xdotool windowraise "$w" 2>/dev/null || true
  xdotool windowfocus "$w" 2>/dev/null || true
  sleep 0.12
  xdotool mousemove --window "$w" 140 120 click 1 2>/dev/null || true
  sleep 0.12
}}

click_start_area_guesses() {{
  local w="$1"
  local geom H
  geom=$(xdotool getwindowgeometry --shell "$w" 2>/dev/null || true)
  H=$(echo "$geom" | grep '^HEIGHT=' | cut -d= -f2 || true)

  # Кнопка "Пуск (Enter)" обычно снизу слева.
  # Жмём несколько вариантов по Y и X (только МЫШЬ!)
  local y1 y2 y3 y4
  local x1 x2 x3
  if [ -n "$H" ]; then
    y1=$((H-45)); y2=$((H-55)); y3=$((H-65)); y4=$((H-75))
  else
    y1=418; y2=408; y3=398; y4=388
  fi
  x1=70; x2=110; x3=150

  log "clicking start button guesses: x=$x1,$x2,$x3 y=$y1,$y2,$y3,$y4 (H=${{H:-?}})"
  for x in $x1 $x2 $x3; do
    for y in $y1 $y2 $y3 $y4; do
      xdotool mousemove --window "$w" "$x" "$y" click 1 2>/dev/null || true
      sleep 0.10
    done
  done
}}

if [ "$NEEDS_ENTER" = "1" ]; then
  if have xdotool && [ -n "$win" ]; then
    log "DrawMan: chosen window=$win"
    focus_and_click "$win"

    # Жмём "Пуск" мышью
    click_start_area_guesses "$win"

    log "waiting AFTER_START=$AFTER_START s"
    sleep "$AFTER_START"
  else
    log "DrawMan: no window or no xdotool -> fallback sleep CAPTURE_DELAY=$CAPTURE_DELAY"
    sleep "$CAPTURE_DELAY"
  fi
else
  log "non-DrawMan: sleep CAPTURE_DELAY=$CAPTURE_DELAY"
  sleep "$CAPTURE_DELAY"
fi

# Если процесс умер — это НЕ успех. Фиксим твою проблему "200 + чёрный экран".
CRASHED="0"
if ! is_alive; then
  CRASHED="1"
  log "mono process is NOT alive before capture -> treating as crash"
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

log "tail program.log (last 160 lines):"
tail -n 160 program.log 2>/dev/null || true

# Cleanup
if is_alive; then
  log "cleanup: kill pid=$pid"
  kill $pid >/dev/null 2>&1 || true
  wait $pid >/dev/null 2>&1 || true
else
  wait $pid >/dev/null 2>&1 || true
fi

# Если был креш — выход с ошибкой (важно для API)
if [ "$CRASHED" = "1" ]; then
  log "===== runner end (CRASH) ====="
  exit 7
fi

# Доп. маркер: если mono_crash*.json появился — тоже ошибка
if ls mono_crash.*.json >/dev/null 2>&1; then
  log "mono crash dump file exists -> failing"
  log "===== runner end (CRASH DUMP) ====="
  exit 8
fi

log "===== runner end (OK) ====="
exit 0
"""


def _read_text_if_exists(p: Path) -> str:
    if p.exists():
        return p.read_text(encoding="utf-8", errors="replace")
    return ""


def _compile(td: str, src_path: Path, timeout: int) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["mono", PABCNETC, str(src_path)],
        cwd=td,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        timeout=timeout,
    )


def _run_script(td: str, run_sh: Path, timeout: int) -> subprocess.CompletedProcess:
    cmd = [
        "xvfb-run",
        "-a",
        "-s",
        f"-screen 0 {SCREEN_W}x{SCREEN_H}x{SCREEN_D}",
        str(run_sh),
    ]
    return subprocess.run(
        cmd,
        cwd=td,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        timeout=timeout,
    )


def _render_impl(req: RenderRequest):
    t0 = time.perf_counter()
    src = req.source or ""
    mode = _detect_mode(src)
    needs_enter = (mode == "DrawMan")

    total_timeout = int(req.timeout_seconds or 1)
    if total_timeout < 1:
        total_timeout = 1

    _log(f"start mode={mode} needs_enter={needs_enter} timeout={total_timeout}s codeLen={len(src)}")
    _log(f"env: PABCNETC={PABCNETC} TF_SCREEN={SCREEN_W}x{SCREEN_H}x{SCREEN_D} TF_TRIM={TRIM_ENV}")

    with tempfile.TemporaryDirectory(prefix="tfr-img-pabcnet-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        out_png = td_path / "out.png"

        # Важно: UTF-8, чтобы DrawMan код с русскими комментами/символами не ломался
        src_path.write_text(src, encoding="utf-8")
        _log(f"write source: {src_path} bytes={src_path.stat().st_size}")

        # Compile
        try:
            compile_timeout = min(60, max(8, total_timeout - 6))
            _log(f"compile: mono pabcnetc ... timeout={compile_timeout}s")
            cp = _compile(td, src_path, compile_timeout)
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "compile timeout")

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

        bash_script = _build_bash_script(
            td=td,
            exe_path=exe_path,
            out_png=out_png,
            needs_enter=needs_enter,
            run_timeout=total_timeout,
        )

        run_sh = td_path / "run.sh"
        run_sh.write_text(bash_script, encoding="utf-8")
        os.chmod(run_sh, 0o755)

        # Run
        try:
            _log(f"run: xvfb-run ... timeout={total_timeout}s")
            rp = _run_script(td, run_sh, total_timeout)
        except subprocess.TimeoutExpired:
            runner_log = _read_text_if_exists(td_path / "runner.log")
            program_log = _read_text_if_exists(td_path / "program.log")
            raise HTTPException(
                504,
                "run timeout\n\n"
                "runner.log:\n" + _tail(runner_log) + "\n\n"
                "program.log:\n" + _tail(program_log),
            )

        if rp.stdout:
            _log("run.sh stdout (tail):\n" + _tail(rp.stdout))

        _log(f"run done exitCode={rp.returncode}")

        runner_log = _read_text_if_exists(td_path / "runner.log")
        program_log = _read_text_if_exists(td_path / "program.log")

        # Если скрипт вернул не 0 — это теперь означает креш/ошибку DrawMan.
        if rp.returncode != 0:
            raise HTTPException(
                400,
                "runtime failed\n\n"
                "runner.log:\n" + _tail(runner_log) + "\n\n"
                "program.log:\n" + _tail(program_log),
            )

        if not out_png.exists() or out_png.stat().st_size == 0:
            raise HTTPException(
                400,
                "failed to capture image (out.png not produced)\n\n"
                "runner.log:\n" + _tail(runner_log) + "\n\n"
                "program.log:\n" + _tail(program_log),
            )

        total = time.perf_counter() - t0
        _log(f"OK mode={mode} totalSec={total:.3f}s -> returning png bytes size={out_png.stat().st_size}")

        return Response(content=out_png.read_bytes(), media_type="image/png")


@app.post("/render")
def render(req: RenderRequest):
    return _render_impl(req)


# Алиас для совместимости: если бэк стучится в /render/debug — всё равно обработаем Паскаль раннером,
# чтобы DrawMan не улетал в python-image-runner из-за “debug endpoint”.
@app.post("/render/debug")
def render_debug(req: RenderRequest):
    return _render_impl(req)
