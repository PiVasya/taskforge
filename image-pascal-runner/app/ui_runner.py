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
from typing import Optional, List, Tuple
import shutil


# =========================================================
# LOGGING: максимально подробные логи в stdout контейнера
# =========================================================
_LOG = logging.getLogger("tf.pascal.ui")
def _run_cmd(cmd, env=None, timeout=10, cwd=None):
    """
    Запуск команды с захватом stdout/stderr.
    Возвращает subprocess.CompletedProcess (cp.returncode / cp.stdout / cp.stderr).
    """
    return subprocess.run(
        cmd,
        env=env,
        cwd=cwd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=timeout,
        check=False,
    )


def _start_window_manager(*, env: dict, log_prefix: str = "") -> Optional[subprocess.Popen]:
    """Start a lightweight WM inside Xvfb.

    Under bare Xvfb (no WM), xdotool windowactivate/windowfocus is unreliable
    and may produce errors like "Your windowmanager claims not to support
    _NET_ACTIVE_WINDOW". DrawMan often needs actual focus to accept Enter/Space.
    """

    for cmd in ("openbox", "fluxbox", "xfwm4", "metacity"):
        if shutil.which(cmd):
            try:
                _LOG.debug("%sWM_START cmd=%s", log_prefix, cmd)
                p = subprocess.Popen(
                    [cmd],
                    env=env,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
                # give WM a moment to become ready
                time.sleep(0.35)
                return p
            except Exception as e:
                _LOG.debug("%sWM_START failed cmd=%s err=%r", log_prefix, cmd, e)
                continue
    _LOG.debug("%sWM_START skipped (no WM found)", log_prefix)
    return None


def _stop_proc(p: Optional[subprocess.Popen], *, name: str, log_prefix: str = "") -> None:
    if not p:
        return
    try:
        _LOG.debug("%sSTOP %s pid=%s", log_prefix, name, p.pid)
        p.terminate()
        try:
            p.wait(timeout=1.5)
        except Exception:
            p.kill()
    except Exception:
        pass


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
    draw_wait_seconds: float


STRATEGIES = {
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


def _merge_env(env: dict | None) -> dict:
    if env is None:
        return os.environ.copy()
    out = os.environ.copy()
    out.update(env)
    return out


def _run(cmd: list[str], timeout: float | None = None, *, env: dict | None = None, log_prefix: str = "") -> subprocess.CompletedProcess:
    _LOG.debug("%s CMD %s (timeout=%s) ENV_DISPLAY=%s", log_prefix, cmd, timeout, (env or {}).get("DISPLAY"))
    cp = subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        timeout=timeout,
        env=_merge_env(env),
    )
    out = (cp.stdout or "").strip()
    _LOG.debug("%s RC=%s OUT_LEN=%s", log_prefix, cp.returncode, len(cp.stdout or ""))
    if out:
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


def _digits_only(tokens: list[str]) -> list[str]:
    # xdotool window ids are decimal numbers
    return [t for t in tokens if t.strip().isdigit()]


def _xdotool_get_geometry(win: str, *, env: dict, log_prefix: str = "") -> tuple[int, int, int, int] | None:
    cp = _run_cmd(["xdotool", "getwindowgeometry", "--shell", win], env=env, timeout=3)
    if cp.returncode != 0:
        return None
    vals = {}
    for line in (cp.stdout or "").splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            vals[k.strip()] = v.strip()
    try:
        x = int(vals.get("X", "0"))
        y = int(vals.get("Y", "0"))
        w = int(vals.get("WIDTH", "0"))
        h = int(vals.get("HEIGHT", "0"))
        return x, y, w, h
    except Exception:
        return None


def _xdotool_get_name(win: str, *, env: dict, log_prefix: str = "") -> str:
    cp = _run_cmd(["xdotool", "getwindowname", win], env=env, timeout=3)
    return (cp.stdout or "").strip()


def _xdotool_search(
    pid: int,
    name_regex: str,
    max_wait: float | None = None,
    *,
    env: dict,
    log_prefix: str = "",
    timeout: float | None = None,
) -> str | None:
    if not _which("xdotool", log_prefix=log_prefix):
        return None

    # Backward/forward compatibility:
    # - older callers pass `max_wait`
    # - some forks pass `timeout` instead
    if max_wait is None:
        max_wait = timeout if timeout is not None else 10.0

    deadline = time.time() + float(max_wait)
    attempt = 0

    # xdotool uses POSIX regex. Depending on build flags, alternation like "a|b" may
    # not work as expected. We therefore derive a few simple tokens and try them one
    # by one as a fallback.
    name_tokens: list[str] = []
    cleaned = name_regex.strip()
    if cleaned.startswith("(") and cleaned.endswith(")"):
        cleaned = cleaned[1:-1].strip()
    if "|" in cleaned:
        for t in cleaned.split("|"):
            t = t.strip()
            if t:
                name_tokens.append(t)
    if not name_tokens:
        name_tokens = [name_regex]
    name_re = None
    try:
        name_re = re.compile(name_regex, re.IGNORECASE)
    except Exception:
        name_re = None

    def pick_best(wins: list[str]) -> str:
        """Choose the most likely real app window.

        xdotool may return multiple windows for one PID (splash, helper, tiny 10x10,
        etc.). We pick:
          1) any window whose title matches name_regex (if we can read the title)
          2) otherwise the largest window by area.
        """
        best = wins[0]
        best_area = -1
        best_match = False
        for w in wins:
            geo = _xdotool_get_geometry(w, env=env, log_prefix=log_prefix)
            if geo:
                _, _, ww, hh = geo
                area = ww * hh
            else:
                area = 0
            title = _xdotool_get_name(w, env=env, log_prefix=log_prefix)
            is_match = bool(name_re.search(title)) if (name_re and title) else False
            if is_match and not best_match:
                best, best_area, best_match = w, area, True
                continue
            if is_match == best_match and area > best_area:
                best, best_area = w, area
        return best

    while time.time() < deadline:
        attempt += 1

        # IMPORTANT:
        # Under Xvfb we may run without a window manager. In that case a top-level
        # window can exist but not be considered "visible" by xdotool.
        # Using --onlyvisible makes the search flaky (DrawMan is exactly this case).
        cp = _run(["xdotool", "search", "--all", "--pid", str(pid)], timeout=3, env=env, log_prefix=log_prefix)
        wins = _digits_only(cp.stdout.split())
        if wins:
            chosen = pick_best(wins)
            _LOG.debug("%s WIN_FOUND by pid attempt=%s => %s (all=%s)", log_prefix, attempt, chosen, wins)
            return chosen

        for token in name_tokens:
            cp = _run(["xdotool", "search", "--all", "--name", token], timeout=3, env=env, log_prefix=log_prefix)
            wins = _digits_only(cp.stdout.split())
            if wins:
                chosen = pick_best(wins)
                _LOG.debug("%s WIN_FOUND by name token=%r attempt=%s => %s (all=%s)", log_prefix, token, attempt, chosen, wins)
                return chosen

        _LOG.debug("%s WIN_SEARCH attempt=%s not found; sleep 100ms", log_prefix, attempt)
        time.sleep(0.1)

    return None


def _xdotool_focus(win: str, *, env: dict, log_prefix: str = "") -> None:
    _LOG.debug("%s FOCUS win=%s", log_prefix, win)
    # With a WM, this usually works. If it doesn't, we still proceed with a click.
    _run(["xdotool", "windowmap", win], timeout=4, env=env, log_prefix=log_prefix)
    _run(["xdotool", "windowraise", win], timeout=4, env=env, log_prefix=log_prefix)
    _run(["xdotool", "windowactivate", "--sync", win], timeout=4, env=env, log_prefix=log_prefix)
    _run(["xdotool", "windowfocus", win], timeout=4, env=env, log_prefix=log_prefix)
    _xdotool_click_center(win, env=env, log_prefix=log_prefix)


def _xdotool_click_center(win: str, *, env: dict, log_prefix: str = "") -> tuple[int, int, int, int] | None:
    if not _which("xdotool", log_prefix=log_prefix):
        return None

    g = _xdotool_get_geometry(win, env=env, log_prefix=log_prefix)
    if not g:
        return None
    x, y, w, h = g
    cx, cy = x + max(10, w // 2), y + max(10, h // 2)
    _LOG.debug("%s CLICK_CENTER geo=(%s,%s,%s,%s) center=(%s,%s)", log_prefix, x, y, w, h, cx, cy)

    _run(["xdotool", "mousemove", str(cx), str(cy)], timeout=3, env=env, log_prefix=log_prefix)
    _run(["xdotool", "click", "1"], timeout=3, env=env, log_prefix=log_prefix)
    return x, y, w, h


def _drawman_start(win: str, geo: tuple[int, int, int, int] | None, *, env: dict, log_prefix: str = "") -> None:
    if not _which("xdotool", log_prefix=log_prefix):
        return

    _LOG.debug("%s DRAWMAN_START step1: Enter x3", log_prefix)
    for i in range(3):
        _LOG.debug("%s DRAWMAN_START Enter iter=%s", log_prefix, i + 1)
        _run(["xdotool", "key", "--window", win, "Return"], timeout=2, env=env, log_prefix=log_prefix)
        _run(["xdotool", "key", "--window", win, "KP_Enter"], timeout=2, env=env, log_prefix=log_prefix)
        time.sleep(0.15)

    space_count = int(os.getenv("TF_DRAWMAN_SPACE_COUNT", "50"))
    space_count = max(10, min(500, space_count))
    _LOG.debug("%s DRAWMAN_START step2: spam spaces count=%s", log_prefix, space_count)
    for i in range(space_count):
        # ЛОГИ НА КАЖДЫЙ МИЛЛИМЕТР: логируем каждый пробел
        _LOG.debug("%s DRAWMAN_START space i=%s/%s", log_prefix, i + 1, space_count)
        _run(["xdotool", "key", "--window", win, "space"], timeout=2, env=env, log_prefix=log_prefix)
        time.sleep(0.02)

    if geo is not None:
        x, y, w, h = geo
        px = x + 40
        py = y + h - 20
        _LOG.debug("%s DRAWMAN_START step3: click start area (%s,%s)", log_prefix, px, py)
        _run(["xdotool", "mousemove", str(px), str(py)], timeout=2, env=env, log_prefix=log_prefix)
        _run(["xdotool", "click", "1"], timeout=2, env=env, log_prefix=log_prefix)
        time.sleep(0.1)
        _run(["xdotool", "key", "--window", win, "Return"], timeout=2, env=env, log_prefix=log_prefix)


def _capture_root(out_png: Path, trim: bool, *, env: dict, log_prefix: str = "") -> None:
    if not _which("import", log_prefix=log_prefix):
        raise RuntimeError("ImageMagick import not found")

    _LOG.debug("%s CAPTURE root => %s", log_prefix, out_png)
    cp = _run(["import", "-window", "root", str(out_png)], timeout=15, env=env, log_prefix=log_prefix)
    if cp.returncode != 0:
        raise RuntimeError("import failed")

    if trim and _which("convert", log_prefix=log_prefix):
        _LOG.debug("%s TRIM png via convert", log_prefix)
        cp2 = _run(["convert", str(out_png), "-trim", "+repage", str(out_png)], timeout=15, env=env, log_prefix=log_prefix)
        if cp2.returncode != 0:
            raise RuntimeError("convert -trim failed")


def _start_xvfb(xvfb_screen: str, timeout: float, *, log_prefix: str = "") -> tuple[subprocess.Popen, dict]:
    # Prefer fixed display so debug is predictable; allow override
    display = os.getenv("TF_DISPLAY", ":99")
    display_num = display.lstrip(":")

    cmd = ["Xvfb", display, "-screen", "0", xvfb_screen, "-nolisten", "tcp", "-ac"]
    _LOG.debug("%s XVFB_START cmd=%s", log_prefix, cmd)

    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)

    def _pump():
        try:
            if p.stdout is None:
                return
            for line in p.stdout:
                s = (line or "").rstrip("\n")
                if s:
                    _LOG.debug("%s XVFB_LINE %s", log_prefix, s)
        except Exception as e:
            _LOG.debug("%s XVFB_PUMP_ERR %s", log_prefix, e)

    threading.Thread(target=_pump, daemon=True).start()

    env = os.environ.copy()
    env["DISPLAY"] = display

    # Wait until socket appears
    sock = Path("/tmp/.X11-unix") / f"X{display_num}"
    deadline = time.time() + timeout
    _LOG.debug("%s XVFB_WAIT sock=%s timeout=%.2fs", log_prefix, sock, timeout)
    while time.time() < deadline:
        if sock.exists():
            _LOG.debug("%s XVFB_READY sock_exists=True", log_prefix)
            return p, env
        if p.poll() is not None:
            _LOG.debug("%s XVFB_EXITED early rc=%s", log_prefix, p.returncode)
            break
        time.sleep(0.05)

    # try xdpyinfo if available for extra hint
    if _which("xdpyinfo", log_prefix=log_prefix):
        _run(["xdpyinfo", "-display", display], timeout=2, env=env, log_prefix=log_prefix)

    raise RuntimeError("Xvfb did not become ready")


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
    """Run compiled exe with a real Xvfb server (NOT xvfb-run) so that:
    - xdotool/import/convert see DISPLAY
    - no 'Can't open display: (null)'
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

    td = exe_path.parent

    # Start Xvfb
    xvfb_budget = min(3.0, max(1.0, timeout_seconds * 0.2))
    xvfb_proc, xenv = _start_xvfb(xvfb_screen, timeout=xvfb_budget, log_prefix=log_prefix)

    # Start a window manager inside Xvfb (crucial for reliable focus/activate).
    wm_proc = _start_window_manager(env=xenv, log_prefix=log_prefix)

    mono_proc: subprocess.Popen | None = None
    try:
        # Run program
        _LOG.debug("%s MONO_START cwd=%s exe=%s program_log=%s DISPLAY=%s", log_prefix, td, exe_path, program_log, xenv.get("DISPLAY"))
        with open(program_log, "w", encoding="utf-8", errors="replace") as f:
            mono_proc = subprocess.Popen(
                ["mono", str(exe_path)],
                cwd=str(td),
                stdout=f,
                stderr=subprocess.STDOUT,
                env=xenv,
            )

        _LOG.debug("%s MONO_PID=%s", log_prefix, mono_proc.pid)

        # Give UI a moment
        time.sleep(0.6)
        _LOG.debug("%s after initial sleep 0.6s", log_prefix)

        # Find window
        win_wait = float(os.getenv("TF_WINDOW_WAIT", "10.0"))
        win_wait = max(1.0, min(20.0, win_wait))
        _LOG.debug("%s WINDOW_SEARCH wait=%ss", log_prefix, win_wait)
        win = _xdotool_search(mono_proc.pid, strat.window_name_regex, max_wait=min(win_wait, timeout_seconds - 1), env=xenv, log_prefix=log_prefix)
        _LOG.debug("%s WINDOW_SEARCH result=%s", log_prefix, win)

        if win is not None:
            _xdotool_focus(win, env=xenv, log_prefix=log_prefix)
            geo = _xdotool_click_center(win, env=xenv, log_prefix=log_prefix)
            _LOG.debug("%s WINDOW_GEO=%s", log_prefix, geo)
            if strat.mode == "DrawMan":
                _drawman_start(win, geo, env=xenv, log_prefix=log_prefix)
        else:
            _LOG.debug("%s WINDOW_NOT_FOUND: skipping focus/start", log_prefix)

        # wait for draw
        wait_s = max(0.2, min(strat.draw_wait_seconds, max(0.5, timeout_seconds - 1.0)))
        _LOG.debug("%s DRAW_WAIT sleep %.3fs", log_prefix, wait_s)
        time.sleep(wait_s)

        _capture_root(out_png, trim=trim, env=xenv, log_prefix=log_prefix)

        # log sizes
        if out_png.exists():
            _LOG.debug("%s out_png exists size=%s", log_prefix, out_png.stat().st_size)
        else:
            _LOG.debug("%s out_png does NOT exist", log_prefix)

        if program_log.exists():
            txt = program_log.read_text(encoding="utf-8", errors="replace")
            if txt.strip():
                _LOG.debug("%s program.log tail:\n%s", log_prefix, txt[-12000:])

    finally:
        # stop mono
        if mono_proc is not None:
            try:
                _LOG.debug("%s STOP mono pid=%s", log_prefix, mono_proc.pid)
                mono_proc.send_signal(signal.SIGTERM)
            except Exception:
                pass
            try:
                mono_proc.wait(timeout=1)
            except Exception:
                try:
                    _LOG.debug("%s KILL mono pid=%s", log_prefix, mono_proc.pid)
                    mono_proc.kill()
                except Exception:
                    pass

        # stop xvfb
        _stop_proc(wm_proc, name="wm", log_prefix=log_prefix)
        try:
            _LOG.debug("%s STOP xvfb pid=%s", log_prefix, xvfb_proc.pid)
            xvfb_proc.send_signal(signal.SIGTERM)
        except Exception:
            pass
        try:
            xvfb_proc.wait(timeout=1)
        except Exception:
            try:
                _LOG.debug("%s KILL xvfb pid=%s", log_prefix, xvfb_proc.pid)
                xvfb_proc.kill()
            except Exception:
                pass
