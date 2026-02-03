import os
import shlex
import signal
import subprocess
import time
import logging
import sys
import threading
from dataclasses import dataclass
from pathlib import Path


# =========================================================
# LOGGING: максимально подробные логи в stdout контейнера
# =========================================================
_LOG = logging.getLogger("tf.pascal.ui")
_LOG.setLevel(logging.DEBUG)
if not _LOG.handlers:
    h = logging.StreamHandler(sys.stdout)
    h.setLevel(logging.DEBUG)
    h.setFormatter(logging.Formatter("%(asctime)s | %(levelname)s | %(message)s"))
    _LOG.addHandler(h)
    _LOG.propagate = False


@dataclass
class UiStrategy:
    mode: str
    window_name_regex: str
    # how long to wait for drawing after “start” action
    draw_wait_seconds: float


STRATEGIES = {
    # DrawMan often shows a “press Enter / many spaces” start screen.
    "DrawMan": UiStrategy(
        mode="DrawMan",
        window_name_regex=r"(Чертежник|Поле|DrawMan|ПаскальАБЦ|PascalABC)",
        draw_wait_seconds=float(os.getenv("TF_DRAWMAN_DRAW_WAIT", "9.5")),
    ),
    "GraphABC": UiStrategy(
        mode="GraphABC",
        window_name_regex=r"(GraphABC|PascalABC)",
        draw_wait_seconds=float(os.getenv("TF_GRAPHABC_DRAW_WAIT", "0.8")),
    ),
    "Pascal": UiStrategy(
        mode="Pascal",
        window_name_regex=r"(PascalABC)",
        draw_wait_seconds=float(os.getenv("TF_PASCAL_DRAW_WAIT", "0.6")),
    ),
}


def _run(cmd: list[str], timeout: float | None = None, *, log_prefix: str = "") -> subprocess.CompletedProcess:
    _LOG.debug("%s CMD %s (timeout=%s)", log_prefix, cmd, timeout)
    cp = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=timeout)
    out = (cp.stdout or "").strip()
    _LOG.debug("%s RC=%s OUT_LEN=%s", log_prefix, cp.returncode, len(cp.stdout or ""))
    if out:
        # Печатаем и голову и хвост, чтобы видно было что происходит
        head = out[:2000]
        tail = out[-4000:] if len(out) > 4000 else ""
        _LOG.debug("%s OUT_HEAD:\n%s", log_prefix, head)
        if tail:
            _LOG.debug("%s OUT_TAIL:\n%s", log_prefix, tail)
    return cp


def _which(name: str, *, log_prefix: str = "") -> bool:
    ok = subprocess.call(["bash", "-lc", f"command -v {shlex.quote(name)} >/dev/null 2>&1"]) == 0
    _LOG.debug("%s WHICH %s => %s", log_prefix, name, ok)
    return ok


def _xdotool_search(pid: int, name_regex: str, max_wait: float, *, log_prefix: str = "") -> str | None:
    """Return a single window id as string."""
    if not _which("xdotool", log_prefix=log_prefix):
        return None

    deadline = time.time() + max_wait
    last_out = ""
    attempt = 0
    while time.time() < deadline:
        attempt += 1
        # Prefer pid-bound windows.
        cp = _run(["xdotool", "search", "--onlyvisible", "--pid", str(pid)], timeout=2, log_prefix=log_prefix)
        last_out = cp.stdout.strip()
        wins = [w for w in last_out.split() if w.strip()]
        if wins:
            _LOG.debug("%s WIN_FOUND by pid on attempt=%s => %s", log_prefix, attempt, wins[0])
            return wins[0]

        # Fallback: any visible window matching name regex.
        cp = _run(["xdotool", "search", "--onlyvisible", "--name", name_regex], timeout=2, log_prefix=log_prefix)
        last_out = cp.stdout.strip()
        wins = [w for w in last_out.split() if w.strip()]
        if wins:
            _LOG.debug("%s WIN_FOUND by name on attempt=%s => %s", log_prefix, attempt, wins[0])
            return wins[0]

        _LOG.debug("%s WIN_SEARCH attempt=%s not found yet; sleep 100ms", log_prefix, attempt)
        time.sleep(0.1)

    return None


def _xdotool_focus(win: str, *, log_prefix: str = "") -> None:
    _LOG.debug("%s FOCUS win=%s", log_prefix, win)
    _run(["xdotool", "windowactivate", "--sync", win], timeout=4, log_prefix=log_prefix)
    _run(["xdotool", "windowfocus", win], timeout=4, log_prefix=log_prefix)


def _xdotool_click_center(win: str, *, log_prefix: str = "") -> tuple[int, int, int, int] | None:
    """Returns (x, y, w, h) or None."""
    if not _which("xdotool", log_prefix=log_prefix):
        return None

    geo = _run(["xdotool", "getwindowgeometry", "--shell", win], timeout=3, log_prefix=log_prefix).stdout
    # Output is like:
    #   X=0
    #   Y=0
    #   WIDTH=1024
    #   HEIGHT=768
    vals: dict[str, int] = {}
    for line in geo.splitlines():
        if "=" not in line:
            continue
        k, v = line.split("=", 1)
        k = k.strip().upper()
        v = v.strip()
        if k in ("X", "Y", "WIDTH", "HEIGHT"):
            try:
                vals[k] = int(v)
            except Exception:
                pass
    if not all(k in vals for k in ("X", "Y", "WIDTH", "HEIGHT")):
        return None

    x, y, w, h = vals["X"], vals["Y"], vals["WIDTH"], vals["HEIGHT"]
    cx, cy = x + max(10, w // 2), y + max(10, h // 2)
    _LOG.debug("%s CLICK_CENTER geo=(%s,%s,%s,%s) center=(%s,%s)", log_prefix, x, y, w, h, cx, cy)
    _run(["xdotool", "mousemove", str(cx), str(cy)], timeout=3, log_prefix=log_prefix)
    _run(["xdotool", "click", "1"], timeout=3, log_prefix=log_prefix)
    return x, y, w, h


def _drawman_start(win: str, geo: tuple[int, int, int, int] | None, *, log_prefix: str = "") -> None:
    """Try multiple safe ways to start DrawMan."""
    if not _which("xdotool", log_prefix=log_prefix):
        return

    # 1) A few Enters (main + keypad).
    _LOG.debug("%s DRAWMAN_START step1: send Enter x3", log_prefix)
    for i in range(3):
        _LOG.debug("%s DRAWMAN_START Enter iter=%s", log_prefix, i + 1)
        _run(["xdotool", "key", "--window", win, "Return"], timeout=2, log_prefix=log_prefix)
        _run(["xdotool", "key", "--window", win, "KP_Enter"], timeout=2, log_prefix=log_prefix)
        time.sleep(0.15)

    # 2) Spam Space (many tasks mention "a bunch of spaces").
    space_count = int(os.getenv("TF_DRAWMAN_SPACE_COUNT", "50"))
    space_count = max(10, min(500, space_count))
    _LOG.debug("%s DRAWMAN_START step2: spam spaces count=%s", log_prefix, space_count)
    for i in range(space_count):
        if i < 5 or i % 25 == 0 or i == space_count - 1:
            _LOG.debug("%s DRAWMAN_START space i=%s/%s", log_prefix, i + 1, space_count)
        _run(["xdotool", "key", "--window", win, "space"], timeout=2, log_prefix=log_prefix)
        time.sleep(0.02)

    # 3) Click bottom-left "Пуск (Enter)" area (works in many DrawMan builds).
    if geo is not None:
        x, y, w, h = geo
        px = x + 40
        py = y + h - 20
        _LOG.debug("%s DRAWMAN_START step3: click bottom-left start area at (%s,%s)", log_prefix, px, py)
        _run(["xdotool", "mousemove", str(px), str(py)], timeout=2, log_prefix=log_prefix)
        _run(["xdotool", "click", "1"], timeout=2, log_prefix=log_prefix)
        time.sleep(0.1)
        _run(["xdotool", "key", "--window", win, "Return"], timeout=2, log_prefix=log_prefix)


def _capture_root(out_png: Path, trim: bool, *, log_prefix: str = "") -> None:
    # Primary: ImageMagick "import".
    if _which("import", log_prefix=log_prefix):
        _LOG.debug("%s CAPTURE root => %s", log_prefix, out_png)
        _run(["import", "-window", "root", str(out_png)], timeout=15, log_prefix=log_prefix)
    else:
        raise RuntimeError("ImageMagick import not found")

    if trim and _which("convert", log_prefix=log_prefix):
        # -trim removes same-colored borders; +repage fixes canvas.
        _LOG.debug("%s TRIM png via convert", log_prefix)
        _run(["convert", str(out_png), "-trim", "+repage", str(out_png)], timeout=15, log_prefix=log_prefix)


def run_ui_and_capture(
    *,
    exe_path: Path,
    out_png: Path,
    program_log: Path,
    mode: str,
    xvfb_screen: str,
    timeout_seconds: int,
    trim: bool,
    log_prefix: str = "",
) -> None:
    """Compile already done. Here we:
    - run exe under Xvfb
    - find window
    - for DrawMan: start by Enter/Space/click
    - capture root window to PNG
    """

    strat = STRATEGIES.get(mode) or STRATEGIES["Pascal"]
    timeout_seconds = max(3, min(120, int(timeout_seconds)))

    _LOG.debug(
        "%s UI_START mode=%s regex=%s draw_wait=%.3f timeout=%ss screen=%s exe=%s",
        log_prefix,
        strat.mode,
        strat.window_name_regex,
        strat.draw_wait_seconds,
        timeout_seconds,
        xvfb_screen,
        exe_path,
    )

    # We run a small bash wrapper under xvfb-run so DISPLAY is set correctly.
    # xvfb-run also guarantees X server lifecycle cleanup.
    wrapper = f"""#!/usr/bin/env bash
set -e
cd {shlex.quote(str(exe_path.parent))}
mono {shlex.quote(str(exe_path))} > {shlex.quote(str(program_log))} 2>&1 &
echo $! > child.pid
wait $! || true
"""

    td = exe_path.parent
    sh_path = td / "_run_child.sh"
    sh_path.write_text(wrapper, encoding="utf-8")
    os.chmod(sh_path, 0o755)

    _LOG.debug("%s wrapper=%s\n%s", log_prefix, sh_path, wrapper)

    cmd = [
        "xvfb-run",
        "-a",
        "-s",
        f"-screen 0 {xvfb_screen}",
        str(sh_path),
    ]

    _LOG.debug("%s XVFB_CMD=%s", log_prefix, cmd)

    # Start xvfb-run wrapper (it spawns the mono child and then waits for it).
    p = subprocess.Popen(cmd, cwd=str(td), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)

    # Параллельно стримим stdout xvfb-run в логи контейнера (НЕ блокируя основной поток)
    def _pump():
        try:
            if p.stdout is None:
                return
            for line in p.stdout:
                if line is None:
                    break
                s = line.rstrip("\n")
                if s:
                    _LOG.debug("%s XVFB_LINE %s", log_prefix, s)
        except Exception as e:
            _LOG.debug("%s XVFB_PUMP_ERR %s", log_prefix, e)

    threading.Thread(target=_pump, daemon=True).start()

    try:
        # Wait until wrapper writes child pid.
        pid_file = td / "child.pid"
        pid: int | None = None
        deadline = time.time() + min(5.0, timeout_seconds * 0.3)
        while time.time() < deadline and pid is None:
            if pid_file.exists():
                try:
                    pid = int(pid_file.read_text().strip())
                except Exception:
                    pid = None
            time.sleep(0.05)

        # (stdout xvfb-run уже стримится через thread)

        if pid is None:
            _LOG.debug("%s child.pid not produced in time. pid_file=%s exists=%s", log_prefix, pid_file, pid_file.exists())
            raise RuntimeError("failed to get child pid")

        _LOG.debug("%s CHILD_PID=%s", log_prefix, pid)

        # Give UI a moment to appear.
        time.sleep(0.6)
        _LOG.debug("%s after initial sleep 0.6s", log_prefix)

        # Find a suitable window.
        win_wait = float(os.getenv("TF_WINDOW_WAIT", "10.0"))
        win_wait = max(1.0, min(20.0, win_wait))
        _LOG.debug("%s WINDOW_SEARCH wait=%ss", log_prefix, win_wait)
        win = _xdotool_search(pid, strat.window_name_regex, max_wait=min(win_wait, timeout_seconds - 1), log_prefix=log_prefix)

        _LOG.debug("%s WINDOW_SEARCH result=%s", log_prefix, win)

        if win is not None:
            _xdotool_focus(win, log_prefix=log_prefix)
            geo = _xdotool_click_center(win, log_prefix=log_prefix)
            _LOG.debug("%s WINDOW_GEO=%s", log_prefix, geo)

            if strat.mode == "DrawMan":
                _drawman_start(win, geo, log_prefix=log_prefix)
        else:
            _LOG.debug("%s WINDOW_NOT_FOUND: skipping focus/start", log_prefix)

        # Wait for the drawing to complete.
        wait_s = max(0.2, min(strat.draw_wait_seconds, max(0.5, timeout_seconds - 1.0)))
        _LOG.debug("%s DRAW_WAIT sleep %.3fs", log_prefix, wait_s)
        time.sleep(wait_s)

        _capture_root(out_png, trim=trim, log_prefix=log_prefix)

        # Доп. логи: размер файла и хвост program.log
        try:
            if out_png.exists():
                _LOG.debug("%s out_png exists size=%s", log_prefix, out_png.stat().st_size)
        except Exception:
            pass
        try:
            if program_log.exists():
                txt = program_log.read_text(encoding="utf-8", errors="replace")
                if txt.strip():
                    _LOG.debug("%s program.log tail:\n%s", log_prefix, txt[-12000:])
        except Exception:
            pass

    finally:
        # Stop wrapper+child (best-effort). xvfb-run will be killed.
        try:
            _LOG.debug("%s stopping xvfb-run process pid=%s", log_prefix, p.pid)
            p.send_signal(signal.SIGTERM)
        except Exception:
            pass
        try:
            p.wait(timeout=1)
        except Exception:
            try:
                _LOG.debug("%s xvfb-run did not exit, killing pid=%s", log_prefix, p.pid)
                p.kill()
            except Exception:
                pass
