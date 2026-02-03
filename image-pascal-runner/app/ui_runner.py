import os
import shlex
import signal
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path


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


def _run(cmd: list[str], timeout: float | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=timeout)


def _which(name: str) -> bool:
    return subprocess.call(["bash", "-lc", f"command -v {shlex.quote(name)} >/dev/null 2>&1"]) == 0


def _xdotool_search(pid: int, name_regex: str, max_wait: float) -> str | None:
    """Return a single window id as string."""
    if not _which("xdotool"):
        return None

    deadline = time.time() + max_wait
    last_out = ""
    while time.time() < deadline:
        # Prefer pid-bound windows.
        cp = _run(["xdotool", "search", "--onlyvisible", "--pid", str(pid)], timeout=2)
        last_out = cp.stdout.strip()
        wins = [w for w in last_out.split() if w.strip()]
        if wins:
            return wins[0]

        # Fallback: any visible window matching name regex.
        cp = _run(["xdotool", "search", "--onlyvisible", "--name", name_regex], timeout=2)
        last_out = cp.stdout.strip()
        wins = [w for w in last_out.split() if w.strip()]
        if wins:
            return wins[0]

        time.sleep(0.1)

    return None


def _xdotool_focus(win: str) -> None:
    _run(["xdotool", "windowactivate", "--sync", win], timeout=4)
    _run(["xdotool", "windowfocus", win], timeout=4)


def _xdotool_click_center(win: str) -> tuple[int, int, int, int] | None:
    """Returns (x, y, w, h) or None."""
    if not _which("xdotool"):
        return None

    geo = _run(["xdotool", "getwindowgeometry", "--shell", win], timeout=3).stdout
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
    _run(["xdotool", "mousemove", str(cx), str(cy)], timeout=3)
    _run(["xdotool", "click", "1"], timeout=3)
    return x, y, w, h


def _drawman_start(win: str, geo: tuple[int, int, int, int] | None) -> None:
    """Try multiple safe ways to start DrawMan."""
    if not _which("xdotool"):
        return

    # 1) A few Enters (main + keypad).
    for _ in range(3):
        _run(["xdotool", "key", "--window", win, "Return"], timeout=2)
        _run(["xdotool", "key", "--window", win, "KP_Enter"], timeout=2)
        time.sleep(0.15)

    # 2) Spam Space (many tasks mention "a bunch of spaces").
    space_count = int(os.getenv("TF_DRAWMAN_SPACE_COUNT", "50"))
    for _ in range(max(10, min(200, space_count))):
        _run(["xdotool", "key", "--window", win, "space"], timeout=2)
        time.sleep(0.02)

    # 3) Click bottom-left "Пуск (Enter)" area (works in many DrawMan builds).
    if geo is not None:
        x, y, w, h = geo
        px = x + 40
        py = y + h - 20
        _run(["xdotool", "mousemove", str(px), str(py)], timeout=2)
        _run(["xdotool", "click", "1"], timeout=2)
        time.sleep(0.1)
        _run(["xdotool", "key", "--window", win, "Return"], timeout=2)


def _capture_root(out_png: Path, trim: bool) -> None:
    # Primary: ImageMagick "import".
    if _which("import"):
        _run(["import", "-window", "root", str(out_png)], timeout=15)
    else:
        raise RuntimeError("ImageMagick import not found")

    if trim and _which("convert"):
        # -trim removes same-colored borders; +repage fixes canvas.
        _run(["convert", str(out_png), "-trim", "+repage", str(out_png)], timeout=15)


def run_ui_and_capture(
    *,
    exe_path: Path,
    out_png: Path,
    program_log: Path,
    mode: str,
    xvfb_screen: str,
    timeout_seconds: int,
    trim: bool,
) -> None:
    """Compile already done. Here we:
    - run exe under Xvfb
    - find window
    - for DrawMan: start by Enter/Space/click
    - capture root window to PNG
    """

    strat = STRATEGIES.get(mode) or STRATEGIES["Pascal"]
    timeout_seconds = max(3, min(120, int(timeout_seconds)))

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

    cmd = [
        "xvfb-run",
        "-a",
        "-s",
        f"-screen 0 {xvfb_screen}",
        str(sh_path),
    ]

    # Start xvfb-run wrapper (it spawns the mono child and then waits for it).
    p = subprocess.Popen(cmd, cwd=str(td), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

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

        if pid is None:
            raise RuntimeError("failed to get child pid")

        # Give UI a moment to appear.
        time.sleep(0.6)

        # Find a suitable window.
        win_wait = float(os.getenv("TF_WINDOW_WAIT", "10.0"))
        win_wait = max(1.0, min(20.0, win_wait))
        win = _xdotool_search(pid, strat.window_name_regex, max_wait=min(win_wait, timeout_seconds - 1))

        if win is not None:
            _xdotool_focus(win)
            geo = _xdotool_click_center(win)

            if strat.mode == "DrawMan":
                _drawman_start(win, geo)

        # Wait for the drawing to complete.
        wait_s = max(0.2, min(strat.draw_wait_seconds, max(0.5, timeout_seconds - 1.0)))
        time.sleep(wait_s)

        _capture_root(out_png, trim=trim)

    finally:
        # Stop wrapper+child (best-effort). xvfb-run will be killed.
        try:
            p.send_signal(signal.SIGTERM)
        except Exception:
            pass
        try:
            p.wait(timeout=1)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass
