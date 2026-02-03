import os
import shutil
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Tuple


# -------------------------
# TOOLS / BINARIES
# -------------------------
XDOTOOL = os.getenv("TF_XDOTOOL", "xdotool")
IMPORT = os.getenv("TF_IMPORT", "import")      # ImageMagick
CONVERT = os.getenv("TF_CONVERT", "convert")   # ImageMagick
MONO = os.getenv("TF_MONO", "mono")

XVFB = os.getenv("TF_XVFB", "Xvfb")
XDPYINFO = os.getenv("TF_XDPYINFO", "xdpyinfo")

DISPLAY = os.getenv("DISPLAY", ":99")
RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

# Strategy tuning
DRAW_WAIT_SECONDS = float(os.getenv("TF_DRAW_WAIT", "0.6"))
WIN_SEARCH_WAIT_SECONDS = float(os.getenv("TF_WIN_WAIT", "14.0"))

# Regex per mode
DRAWMAN_REGEX = os.getenv("TF_DRAWMAN_WIN_REGEX", r"DrawMan|Робот|Черепаха|Рисователь")
GRAPHABC_REGEX = os.getenv("TF_GRAPHABC_WIN_REGEX", r"GraphABC|Графика|Графический")


@dataclass
class UiStrategy:
    window_name_regex: str
    win_search_wait: float = WIN_SEARCH_WAIT_SECONDS
    draw_wait_seconds: float = DRAW_WAIT_SECONDS


def _env_base() -> dict:
    e = os.environ.copy()
    e["DISPLAY"] = DISPLAY
    e["LANG"] = RUN_LANG
    e["LC_ALL"] = RUN_LC_ALL
    return e


def _run_cmd(cmd, *, env=None, timeout=10, cwd: Optional[str] = None) -> subprocess.CompletedProcess:
    return subprocess.run(
        cmd,
        env=env,
        cwd=cwd,
        timeout=timeout,
        capture_output=True,
        text=True,
    )


def _log(program_log: Path, msg: str):
    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    program_log.parent.mkdir(parents=True, exist_ok=True)
    with program_log.open("a", encoding="utf-8", errors="replace") as f:
        f.write(f"{ts} | {msg}\n")


def _require_tools():
    missing = []
    for t in [XDOTOOL, IMPORT, CONVERT, XDPYINFO, XVFB]:
        if shutil.which(t) is None:
            missing.append(t)
    if shutil.which(MONO) is None:
        missing.append(MONO)
    if missing:
        raise RuntimeError(f"Missing tools in container: {', '.join(missing)}")


def _display_ready(env: dict, program_log: Path) -> bool:
    cp = _run_cmd([XDPYINFO, "-display", env.get("DISPLAY", DISPLAY)], env=env, timeout=2)
    ok = cp.returncode == 0
    if not ok:
        _log(program_log, f"xdpyinfo NOT ready rc={cp.returncode} err={cp.stderr.strip()[:200]}")
    return ok


def _ensure_xvfb(env: dict, xvfb_screen: str, program_log: Path, log_prefix: str) -> subprocess.Popen:
    """
    Ensure X server exists on DISPLAY. If not, start Xvfb and wait until ready.
    Returns xvfb process (may be running newly started). If already ready, returns None.
    """
    if _display_ready(env, program_log):
        _log(program_log, f"{log_prefix} X display already ready: {env.get('DISPLAY')}")
        return None

    disp = env.get("DISPLAY", DISPLAY)
    screen = xvfb_screen or "1024x768x24"

    # clean stale lock if exists (sometimes after crash)
    # for :99 -> /tmp/.X99-lock
    try:
        if disp.startswith(":"):
            lock = Path("/tmp") / f".X{disp[1:]}-lock"
            if lock.exists():
                _log(program_log, f"{log_prefix} removing stale lock {lock}")
                lock.unlink(missing_ok=True)
    except Exception:
        pass

    cmd = [
        XVFB,
        disp,
        "-screen", "0", screen,
        "-nolisten", "tcp",
        "-ac",
    ]
    _log(program_log, f"{log_prefix} starting Xvfb: {' '.join(cmd)}")

    p = subprocess.Popen(
        cmd,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )

    deadline = time.time() + 8.0
    while time.time() < deadline:
        if _display_ready(env, program_log):
            _log(program_log, f"{log_prefix} Xvfb ready on {disp}")
            return p
        time.sleep(0.2)

    # dump xvfb output tail if any
    try:
        out = ""
        if p.stdout:
            while True:
                line = p.stdout.readline()
                if not line:
                    break
                out += line
                if len(out) > 2000:
                    out = out[-2000:]
        if out:
            _log(program_log, f"{log_prefix} Xvfb output tail:\n{out}")
    except Exception:
        pass

    raise RuntimeError(f"Xvfb did not become ready on DISPLAY={disp}")


def _xdotool_window_exists(win: str, *, env: dict) -> bool:
    cp = _run_cmd([XDOTOOL, "getwindowname", win], env=env, timeout=2)
    return cp.returncode == 0 and (cp.stdout or "").strip() != ""


def _xdotool_search_by_pid(pid: int, name_regex: str, *, env: dict, max_wait: float, program_log: Path) -> Optional[str]:
    deadline = time.time() + max_wait
    while time.time() < deadline:
        cp = _run_cmd([XDOTOOL, "search", "--pid", str(pid), "--name", name_regex], env=env, timeout=2)
        if cp.returncode == 0:
            wins = [w.strip() for w in (cp.stdout or "").split() if w.strip()]
            if wins:
                _log(program_log, f"win found pid={pid} regex={name_regex} win={wins[0]}")
                return wins[0]
        time.sleep(0.2)
    _log(program_log, f"win NOT found pid={pid} regex={name_regex} (waited {max_wait}s)")
    return None


def _xdotool_focus(win: str, *, env: dict, program_log: Path):
    _log(program_log, f"focus win={win}")
    _run_cmd([XDOTOOL, "windowactivate", "--sync", win], env=env, timeout=4)


def _xdotool_click_center(win: str, *, env: dict, program_log: Path):
    _log(program_log, f"click center win={win}")
    _run_cmd([XDOTOOL, "mousemove", "--window", win, "50%", "50%"], env=env, timeout=3)
    _run_cmd([XDOTOOL, "click", "--window", win, "1"], env=env, timeout=3)


def _safe_get_win(pid: int, strat: UiStrategy, *, env: dict, program_log: Path) -> Optional[str]:
    return _xdotool_search_by_pid(pid, strat.window_name_regex, env=env, max_wait=strat.win_search_wait, program_log=program_log)


def _safe_key(pid: int, win: Optional[str], key: str, strat: UiStrategy, *, env: dict, program_log: Path) -> Optional[str]:
    if (not win) or (not _xdotool_window_exists(win, env=env)):
        win = _safe_get_win(pid, strat, env=env, program_log=program_log)
        if not win:
            return None

    _log(program_log, f"send key='{key}' win={win}")
    cp = _run_cmd([XDOTOOL, "key", "--window", win, "--clearmodifiers", key], env=env, timeout=2)
    if cp.returncode == 0:
        return win

    # retry once
    win2 = _safe_get_win(pid, strat, env=env, program_log=program_log)
    if not win2:
        return win
    _log(program_log, f"retry key='{key}' win={win2}")
    _run_cmd([XDOTOOL, "key", "--window", win2, "--clearmodifiers", key], env=env, timeout=2)
    return win2


def _xdotool_get_geometry(win: str, *, env: dict, program_log: Path) -> Optional[Tuple[int, int, int, int]]:
    cp = _run_cmd([XDOTOOL, "getwindowgeometry", "--shell", win], env=env, timeout=3)
    if cp.returncode != 0:
        _log(program_log, f"getwindowgeometry failed win={win} err={cp.stderr.strip()[:300]}")
        return None

    x = y = w = h = None
    for line in (cp.stdout or "").splitlines():
        line = line.strip()
        if line.startswith("X="):
            x = int(line[2:])
        elif line.startswith("Y="):
            y = int(line[2:])
        elif line.startswith("WIDTH="):
            w = int(line[6:])
        elif line.startswith("HEIGHT="):
            h = int(line[7:])
    if None in (x, y, w, h):
        _log(program_log, f"geometry parse failed win={win} stdout={cp.stdout!r}")
        return None

    _log(program_log, f"geometry win={win} x={x} y={y} w={w} h={h}")
    return x, y, w, h


def _capture_root_crop(win: str, out_png: Path, *, env: dict, trim: bool, program_log: Path):
    g = _xdotool_get_geometry(win, env=env, program_log=program_log)
    if not g:
        raise RuntimeError("Cannot get window geometry")
    x, y, w, h = g
    if w <= 0 or h <= 0:
        raise RuntimeError(f"Bad geometry: {g}")

    root_png = out_png.with_suffix(".root.png")

    _log(program_log, f"capture root -> {root_png}")
    cp1 = _run_cmd([IMPORT, "-window", "root", str(root_png)], env=env, timeout=20)
    if cp1.returncode != 0:
        raise RuntimeError(f"import root failed: {cp1.stderr.strip()[:400]}")

    crop_arg = f"{w}x{h}+{x}+{y}"
    cmd = [CONVERT, str(root_png), "-crop", crop_arg, "+repage"]
    if trim:
        cmd += ["-trim", "+repage"]
    cmd += [str(out_png)]

    _log(program_log, f"convert crop -> {out_png} cmd={' '.join(cmd)}")
    cp2 = _run_cmd(cmd, env=env, timeout=30)
    if cp2.returncode != 0:
        raise RuntimeError(f"convert crop failed: {cp2.stderr.strip()[:400]}")


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
):
    """
    Expected by app/main.py

    mode: DrawMan | GraphABC | Pascal
    """
    _require_tools()
    env = _env_base()

    mode_norm = (mode or "").strip().lower()
    if mode_norm == "drawman":
        strat = UiStrategy(window_name_regex=DRAWMAN_REGEX)
    elif mode_norm == "graphabc":
        strat = UiStrategy(window_name_regex=GRAPHABC_REGEX)
    else:
        strat = UiStrategy(window_name_regex=DRAWMAN_REGEX + "|" + GRAPHABC_REGEX)

    _log(program_log, f"{log_prefix} run_ui_and_capture start mode={mode} exe={exe_path} timeout={timeout_seconds}s screen={xvfb_screen} trim={trim}")
    _log(program_log, f"{log_prefix} env DISPLAY={env.get('DISPLAY')} LANG={env.get('LANG')} LC_ALL={env.get('LC_ALL')}")

    xvfb_proc = None
    try:
        # ВАЖНО: гарантируем Xvfb (иначе WinForms/GraphABC падает с "Could not open display")
        xvfb_proc = _ensure_xvfb(env, xvfb_screen, program_log, log_prefix)

        # запуск программы
        cmd = [MONO, str(exe_path)]
        _log(program_log, f"{log_prefix} POPEN cmd={' '.join(cmd)} cwd={exe_path.parent}")

        p = subprocess.Popen(
            cmd,
            env=env,
            cwd=str(exe_path.parent),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )

        t_start = time.time()
        try:
            # ждём окно
            win = _safe_get_win(p.pid, strat, env=env, program_log=program_log)
            if not win:
                raise RuntimeError(f"Window not found for pid={p.pid}, regex={strat.window_name_regex}")

            _xdotool_focus(win, env=env, program_log=program_log)
            _xdotool_click_center(win, env=env, program_log=program_log)

            # DrawMan — НЕ жмём Space. Только Enter один раз.
            if mode_norm == "drawman":
                win = _safe_key(p.pid, win, "Return", strat, env=env, program_log=program_log)
                if not win:
                    raise RuntimeError("Failed to send Enter to DrawMan window")

            # подождать чтобы дорисовало
            time.sleep(strat.draw_wait_seconds)

            # скрин+кроп
            try:
                _capture_root_crop(win, out_png, env=env, trim=trim, program_log=program_log)
            except Exception as e:
                _log(program_log, f"{log_prefix} capture failed with trim={trim}: {e} -> retry without trim")
                _capture_root_crop(win, out_png, env=env, trim=False, program_log=program_log)

            if not out_png.exists() or out_png.stat().st_size == 0:
                raise RuntimeError("Capture produced empty out.png")

            _log(program_log, f"{log_prefix} OK out_png={out_png} bytes={out_png.stat().st_size}")

        finally:
            # сливаем stdout процесса в program.log (хвост)
            try:
                if p.stdout:
                    out = ""
                    while True:
                        if time.time() - t_start > max(1, timeout_seconds):
                            break
                        line = p.stdout.readline()
                        if not line:
                            break
                        out += line
                        if len(out) > 20000:
                            out = out[-20000:]
                    if out:
                        _log(program_log, f"{log_prefix} program stdout_tail:\n{out}")
            except Exception:
                pass

            # убиваем процесс (DrawMan/GraphABC часто висит)
            try:
                if p.poll() is None:
                    _log(program_log, f"{log_prefix} terminate pid={p.pid}")
                    p.terminate()
                    try:
                        p.wait(timeout=2)
                    except Exception:
                        _log(program_log, f"{log_prefix} kill pid={p.pid}")
                        p.kill()
            except Exception:
                pass

    finally:
        # гасим Xvfb, если мы его поднимали сами
        if xvfb_proc is not None:
            try:
                _log(program_log, f"{log_prefix} stop Xvfb pid={xvfb_proc.pid}")
                xvfb_proc.terminate()
                try:
                    xvfb_proc.wait(timeout=2)
                except Exception:
                    xvfb_proc.kill()
            except Exception:
                pass
