import os
import shlex
import signal
import subprocess
import time
import re
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


def _has_cmd(name: str) -> bool:
    """Cheap availability check for external commands."""
    return shutil.which(name) is not None


def _parse_xvfb_screen(screen: str) -> tuple[int, int] | None:
    """Parse '1024x768x24' -> (1024, 768)."""
    m = re.match(r"^\s*(\d+)x(\d+)x(\d+)\s*$", screen)
    if not m:
        return None
    try:
        return int(m.group(1)), int(m.group(2))
    except Exception:
        return None


def _xwininfo_tree_windows(*, env: dict, log_prefix: str = "") -> list[tuple[str, str, int, int, int, int]]:
    """Return [(win_dec, title, w, h, x, y), ...] from `xwininfo -root -tree`.

    This is a fallback for cases when xdotool only sees a tiny helper window.
    """
    if not _has_cmd("xwininfo"):
        return []
    try:
        cp = _run_cmd(["xwininfo", "-root", "-tree"], env=env, timeout=3)
    except Exception:
        return []
    if cp.returncode != 0:
        return []

    out: list[tuple[str, str, int, int, int, int]] = []
    # Example line (varies):
    #  0x3e00007 "Castle (symmetric)": ("main.exe" "Main.exe")  400x400+90+88  +90+88
    rx = re.compile(r"\s*(0x[0-9a-fA-F]+)\s+\"([^\"]*)\".*?\s(\d+)x(\d+)\+(-?\d+)\+(-?\d+)")
    for line in (cp.stdout or "").splitlines():
        m = rx.search(line)
        if not m:
            continue
        hid, title, ws, hs, xs, ys = m.group(1), m.group(2), m.group(3), m.group(4), m.group(5), m.group(6)
        try:
            win_dec = str(int(hid, 16))
            w = int(ws)
            h = int(hs)
            x = int(xs)
            y = int(ys)
            out.append((win_dec, title.strip(), w, h, x, y))
        except Exception:
            continue
    if out:
        _LOG.debug("%s XWININFO_TREE windows=%s", log_prefix, len(out))
    return out


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


def _xdotool_get_activewindow(*, env: dict, log_prefix: str = "") -> Optional[str]:
    """Return current active window id (decimal) inside DISPLAY."""
    cp = _run_cmd(["xdotool", "getactivewindow"], env=env, timeout=2)
    if cp.returncode != 0:
        return None
    win = (cp.stdout or "").strip()
    return win if win.isdigit() else None


def _xdotool_search(
    pid: int,
    name_regex: str,
    max_wait: float | None = None,
    *,
    env: dict,
    log_prefix: str = "",
    timeout: float | None = None,
    screen_size: tuple[int, int] | None = None,
) -> str | None:
    if not _which("xdotool", log_prefix=log_prefix):
        return None

    # Backward/forward compatibility:
    if max_wait is None:
        max_wait = timeout if timeout is not None else 10.0

    deadline = time.time() + float(max_wait)
    attempt = 0

    # xdotool uses POSIX regex. Alternation sometimes fails in some builds,
    # поэтому разбиваем на токены.
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

    # Порог “реального” окна (главное лечит 10x10 stub)
    min_w = int(os.getenv("TF_MIN_WIN_W", "200"))
    min_h = int(os.getenv("TF_MIN_WIN_H", "150"))

    def _title_bonus(title: str) -> int:
        if not title:
            return 0
        b = 0
        # общий бонус за совпадение regex
        if name_re and name_re.search(title):
            b += 10_000_000
        # особые бонусы для DrawMan
        if "чертежник" in title.lower():
            b += 20_000_000
        if "поле" in title.lower():
            b += 5_000_000
        return b

    def pick_best(wins: list[str]) -> str:
        """Choose the most likely real app window."""
        best = wins[0]
        best_score = -1

        for w in wins:
            geo = _xdotool_get_geometry(w, env=env, log_prefix=log_prefix)
            if geo:
                _, _, ww, hh = geo
                area = max(0, ww) * max(0, hh)
            else:
                ww = hh = 0
                area = 0

            title = _xdotool_get_name(w, env=env, log_prefix=log_prefix)

            # базовый скор = площадь + бонусы по заголовку
            score = area + _title_bonus(title)

            # Жёстко режем совсем мелкие окна
            if ww < 80 or hh < 80:
                score //= 200

            # режем “псевдо-десктоп” окна без заголовка на весь экран
            if screen_size and (not title or title.strip() == ""):
                sw, sh = screen_size
                if sw > 0 and sh > 0 and ww >= int(sw * 0.92) and hh >= int(sh * 0.92):
                    score //= 200

            _LOG.debug("%s WIN_SCORE win=%s geo=%s title=%r score=%s", log_prefix, w, geo, title, score)

            if score > best_score:
                best_score = score
                best = w

        return best

    def _gather_candidates() -> list[str]:
        """Gather window ids from multiple heuristics."""
        found: list[str] = []

        # 1) By PID (may include helpers).
        cp = _run(["xdotool", "search", "--onlyvisible", "--all", "--pid", str(pid)], timeout=3, env=env, log_prefix=log_prefix)
        for w in _digits_only(cp.stdout.split()):
            if w not in found:
                found.append(w)

        # 2) By name tokens (more reliable when window PID differs).
        for token in name_tokens:
            cp = _run(["xdotool", "search", "--onlyvisible", "--all", "--name", token], timeout=3, env=env, log_prefix=log_prefix)
            for w in _digits_only(cp.stdout.split()):
                if w not in found:
                    found.append(w)

        # 3) Fallback: any visible windows
        cp = _run(["xdotool", "search", "--onlyvisible", "--all", "--name", ".*"], timeout=3, env=env, log_prefix=log_prefix)
        for w in _digits_only(cp.stdout.split()):
            if w not in found:
                found.append(w)

        return found

    def _is_usable(win: str) -> bool:
        geo = _xdotool_get_geometry(win, env=env, log_prefix=log_prefix)
        if not geo:
            return False
        _, _, ww, hh = geo
        if ww >= min_w and hh >= min_h:
            return True
        # allow smaller only if title matches strongly
        title = _xdotool_get_name(win, env=env, log_prefix=log_prefix)
        return bool(name_re and title and name_re.search(title))

    while time.time() < deadline:
        attempt += 1

        wins = _gather_candidates()
        if wins:
            chosen = pick_best(wins)
            geo = _xdotool_get_geometry(chosen, env=env, log_prefix=log_prefix)
            _LOG.debug("%s WIN_CANDIDATES attempt=%s chosen=%s geo=%s all=%s", log_prefix, attempt, chosen, geo, wins)

            # Если это опять 10x10/мусор — пробуем xwininfo-tree как “истину”
            if _is_usable(chosen):
                return chosen
            _LOG.debug("%s WIN_TOO_SMALL attempt=%s chosen=%s; try xwininfo fallback", log_prefix, attempt, chosen)

            # xwininfo fallback (исправленный)
            try:
                xwins = _xwininfo_tree_windows(env=env, log_prefix=log_prefix)
                if xwins:
                    # сначала окна, которые матчятся по regex
                    candidates = []
                    for win_dec, title, w, h, x, y in xwins:
                        if name_re and title and name_re.search(title):
                            candidates.append((win_dec, title, w, h, x, y))

                    # если по regex ничего — берём вообще любые (лучше чем 10x10)
                    if not candidates:
                        candidates = xwins

                    best = None
                    best_score = -1
                    for win_dec, title, w, h, x, y in candidates:
                        if w < min_w or h < min_h:
                            continue
                        if screen_size and w >= screen_size[0] - 5 and h >= screen_size[1] - 5:
                            continue

                        score = (w * h) + _title_bonus(title)
                        if score > best_score:
                            best_score = score
                            best = win_dec

                    if best is not None:
                        _LOG.debug("%s XWININFO_FALLBACK picked=%s score=%s", log_prefix, best, best_score)
                        return best
            except Exception as e:
                _LOG.debug("%s XWININFO_FALLBACK error=%r", log_prefix, e)

        _LOG.debug("%s WIN_SEARCH attempt=%s not found/usable; sleep 120ms", log_prefix, attempt)
        time.sleep(0.12)

    return None


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


def _xdotool_focus(win: str, *, env: dict, log_prefix: str = "") -> None:
    _LOG.debug("%s FOCUS win=%s", log_prefix, win)
    _run(["xdotool", "windowmap", win], timeout=4, env=env, log_prefix=log_prefix)
    _run(["xdotool", "windowraise", win], timeout=4, env=env, log_prefix=log_prefix)
    _run(["xdotool", "windowactivate", "--sync", win], timeout=4, env=env, log_prefix=log_prefix)
    _run(["xdotool", "windowfocus", win], timeout=4, env=env, log_prefix=log_prefix)
    _xdotool_click_center(win, env=env, log_prefix=log_prefix)


def _drawman_start(win: str, geo: tuple[int, int, int, int] | None, *, env: dict, log_prefix: str = "") -> None:
    if not _which("xdotool", log_prefix=log_prefix):
        return

    # Try to hit the 'Пуск (Enter)' button area first (more reliable than keys-only on some WMs).
    if geo is not None:
        x, y, w, h = geo
        px = x + 70
        py = y + h - 55
        _LOG.debug("%s DRAWMAN_START pre-click PUSK area (%s,%s)", log_prefix, px, py)
        _run(["xdotool", "mousemove", str(px), str(py)], timeout=2, env=env, log_prefix=log_prefix)
        _run(["xdotool", "click", "1"], timeout=2, env=env, log_prefix=log_prefix)
        time.sleep(0.12)

    _LOG.debug("%s DRAWMAN_START step1: Enter x3", log_prefix)
    for i in range(3):
        _LOG.debug("%s DRAWMAN_START Enter iter=%s", log_prefix, i + 1)
        _run(["xdotool", "key", "--window", win, "--clearmodifiers", "Return"], timeout=2, env=env, log_prefix=log_prefix)
        _run(["xdotool", "key", "--window", win, "--clearmodifiers", "KP_Enter"], timeout=2, env=env, log_prefix=log_prefix)
        time.sleep(0.15)

    # Optional: space spam (оставил как у тебя, но теперь это не единственная надежда)
    space_count = int(os.getenv("TF_DRAWMAN_SPACE_COUNT", "50"))
    space_count = max(0, min(500, space_count))
    _LOG.debug("%s DRAWMAN_START step2: spam spaces count=%s", log_prefix, space_count)
    for i in range(space_count):
        _LOG.debug("%s DRAWMAN_START space i=%s/%s", log_prefix, i + 1, space_count)
        _run(["xdotool", "key", "--window", win, "--clearmodifiers", "space"], timeout=2, env=env, log_prefix=log_prefix)
        time.sleep(0.02)

    if geo is not None:
        x, y, w, h = geo
        px = x + 40
        py = y + h - 20
        _LOG.debug("%s DRAWMAN_START step3: click start area (%s,%s)", log_prefix, px, py)
        _run(["xdotool", "mousemove", str(px), str(py)], timeout=2, env=env, log_prefix=log_prefix)
        _run(["xdotool", "click", "1"], timeout=2, env=env, log_prefix=log_prefix)
        time.sleep(0.1)
        _run(["xdotool", "key", "--window", win, "--clearmodifiers", "Return"], timeout=2, env=env, log_prefix=log_prefix)

    # ВАЖНО: DrawMan часто рисует не мгновенно — добавляем обязательную паузу
    after_delay = float(os.getenv("TF_DRAWMAN_AFTER_START_DELAY", "10.0"))
    after_delay = max(0.0, min(20.0, after_delay))
    _LOG.debug("%s DRAWMAN_START after_delay=%.2fs", log_prefix, after_delay)
    time.sleep(after_delay)


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


def _capture_window(win: str, out_png: Path, trim: bool, *, env: dict, log_prefix: str = "") -> None:
    """Capture a specific X11 window to PNG."""
    if not _which("import", log_prefix=log_prefix):
        raise RuntimeError("ImageMagick import not found")

    try:
        wid_hex = hex(int(str(win), 10))
    except Exception:
        wid_hex = str(win)

    _LOG.debug("%s CAPTURE window=%s => %s", log_prefix, wid_hex, out_png)
    cp = _run(["import", "-window", wid_hex, str(out_png)], timeout=15, env=env, log_prefix=log_prefix)
    if cp.returncode != 0:
        raise RuntimeError("import -window failed")

    if trim and _which("convert", log_prefix=log_prefix):
        _run(["convert", str(out_png), "-trim", "+repage", str(out_png)], timeout=20, env=env, log_prefix=log_prefix)


def _capture_root_crop(win: str, out_png: Path, trim: bool, *, env: dict, log_prefix: str = "") -> None:
    """Fallback capture: grab root, then crop to window geometry to avoid black borders."""
    tmp_root = out_png.with_suffix(out_png.suffix + ".root.png")
    _capture_root(tmp_root, trim=False, env=env, log_prefix=log_prefix)

    geo = _xdotool_get_geometry(win, env=env, log_prefix=log_prefix)
    if geo and _which("convert", log_prefix=log_prefix):
        x, y, w, h = geo
        _LOG.debug("%s CROP root->win geo=%s", log_prefix, geo)
        cp = _run(["convert", str(tmp_root), "-crop", f"{w}x{h}+{x}+{y}", "+repage", str(out_png)], timeout=20, env=env, log_prefix=log_prefix)
        if cp.returncode != 0:
            tmp_root.replace(out_png)
    else:
        tmp_root.replace(out_png)

    if trim and _which("convert", log_prefix=log_prefix):
        _run(["convert", str(out_png), "-trim", "+repage", str(out_png)], timeout=20, env=env, log_prefix=log_prefix)

    try:
        if tmp_root.exists():
            tmp_root.unlink()
    except Exception:
        pass


def _start_xvfb(xvfb_screen: str, timeout: float, *, log_prefix: str = "") -> tuple[subprocess.Popen, dict]:
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
    """Run compiled exe with a real Xvfb server."""
    strat = STRATEGIES.get(mode) or STRATEGIES["Pascal"]
    timeout_seconds = max(3, min(120, int(timeout_seconds)))
    screen_size = _parse_xvfb_screen(xvfb_screen)

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

    # Start WM
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
        time.sleep(0.9)
        _LOG.debug("%s after initial sleep 0.9s", log_prefix)

        # Find window
        if strat.mode == "DrawMan":
            win_wait = float(os.getenv("TF_WINDOW_WAIT", "14.0"))
        else:
            win_wait = float(os.getenv("TF_WINDOW_WAIT", "10.0"))
        win_wait = max(1.0, min(25.0, win_wait))

        _LOG.debug("%s WINDOW_SEARCH wait=%ss", log_prefix, win_wait)
        win = _xdotool_search(
            mono_proc.pid,
            strat.window_name_regex,
            max_wait=min(win_wait, max(1.0, timeout_seconds - 1)),
            env=xenv,
            log_prefix=log_prefix,
            screen_size=screen_size,
        )
        _LOG.debug("%s WINDOW_SEARCH result=%s", log_prefix, win)

        if win is not None:
            _xdotool_focus(win, env=xenv, log_prefix=log_prefix)

            # If active differs - switch
            active = _xdotool_get_activewindow(env=xenv, log_prefix=log_prefix)
            if active and active != win:
                _LOG.debug("%s ACTIVE_WIN switch %s -> %s", log_prefix, win, active)
                win = active

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

        # Capture
        if win is not None:
            try:
                _capture_window(win, out_png, trim=trim, env=xenv, log_prefix=log_prefix)
            except Exception as e:
                _LOG.warning(f"{log_prefix}CAPTURE_WINDOW failed: {e}; falling back to root crop")
                _capture_root_crop(win, out_png, trim=trim, env=xenv, log_prefix=log_prefix)
        else:
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

        _stop_proc(wm_proc, name="wm", log_prefix=log_prefix)

        # stop xvfb
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
