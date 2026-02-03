import os
import re
import shutil
import subprocess
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Tuple

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

app = FastAPI(title="taskforge image pascal runner (DrawMan safe capture)")

# -------------------------
# ENV / CONFIG
# -------------------------
PABCNETC = os.getenv("PABCNETC", "/opt/pabcnetc/pabcnetc.exe")

SCREEN_W = int(os.getenv("TF_SCREEN_W", "1024"))
SCREEN_H = int(os.getenv("TF_SCREEN_H", "768"))
SCREEN_D = int(os.getenv("TF_SCREEN_D", "24"))

RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

DISPLAY = os.getenv("DISPLAY", ":99")

# Strategy tuning
DRAW_WAIT_SECONDS = float(os.getenv("TF_DRAWMAN_DRAW_WAIT", "0.6"))
WIN_SEARCH_WAIT_SECONDS = float(os.getenv("TF_DRAWMAN_WIN_WAIT", "14.0"))

# Regex for DrawMan window title (tweak if needed)
DEFAULT_DRAWMAN_WINDOW_REGEX = os.getenv("TF_DRAWMAN_WIN_REGEX", r"DrawMan|Робот|Черепаха|Рисователь")

# Mono runner (most containers run PascalABC.NET output through mono)
MONO = os.getenv("TF_MONO", "mono")

# Tool paths (expected in image)
XDOTOOL = os.getenv("TF_XDOTOOL", "xdotool")
IMPORT = os.getenv("TF_IMPORT", "import")      # ImageMagick
CONVERT = os.getenv("TF_CONVERT", "convert")   # ImageMagick


class RenderRequest(BaseModel):
    source: str = Field(..., description="PascalABC.NET source code (GraphABC / DrawMan)")
    timeout_seconds: int = Field(20, ge=1, le=120)
    debug: bool = Field(False, description="Enable verbose logs")


@dataclass
class UiStrategy:
    window_name_regex: str = DEFAULT_DRAWMAN_WINDOW_REGEX
    win_search_wait: float = WIN_SEARCH_WAIT_SECONDS
    draw_wait_seconds: float = DRAW_WAIT_SECONDS


# -------------------------
# Helpers
# -------------------------
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


def _require_tools():
    missing = []
    for t in [XDOTOOL, IMPORT, CONVERT]:
        if shutil.which(t) is None:
            missing.append(t)
    if shutil.which(MONO) is None:
        # mono might be optional depending on how you run compiled output
        missing.append(MONO)
    if missing:
        raise HTTPException(status_code=500, detail=f"Missing tools in container: {', '.join(missing)}")


def _xdotool_window_exists(win: str, *, env: dict) -> bool:
    cp = _run_cmd([XDOTOOL, "getwindowname", win], env=env, timeout=2)
    return cp.returncode == 0 and (cp.stdout or "").strip() != ""


def _xdotool_search_by_pid(pid: int, name_regex: str, *, env: dict, max_wait: float) -> Optional[str]:
    """
    Search for first window matching pid+regex.
    """
    deadline = time.time() + max_wait
    while time.time() < deadline:
        cp = _run_cmd([XDOTOOL, "search", "--pid", str(pid), "--name", name_regex], env=env, timeout=2)
        if cp.returncode == 0:
            wins = [w.strip() for w in (cp.stdout or "").split() if w.strip()]
            if wins:
                return wins[0]
        time.sleep(0.2)
    return None


def _xdotool_focus(win: str, *, env: dict):
    _run_cmd([XDOTOOL, "windowactivate", "--sync", win], env=env, timeout=4)


def _xdotool_click_center(win: str, *, env: dict):
    # Move to center and click (helps when focus is flaky)
    _run_cmd([XDOTOOL, "mousemove", "--window", win, "50%", "50%"], env=env, timeout=3)
    _run_cmd([XDOTOOL, "click", "--window", win, "1"], env=env, timeout=3)


def _safe_get_win(pid: int, strat: UiStrategy, *, env: dict) -> Optional[str]:
    return _xdotool_search_by_pid(pid, strat.window_name_regex, env=env, max_wait=strat.win_search_wait)


def _safe_key(pid: int, win: Optional[str], key: str, strat: UiStrategy, *, env: dict) -> Optional[str]:
    """
    Send key to window reliably:
    - if win missing/dead => re-search
    - if send fails => re-search once and retry
    """
    if (not win) or (not _xdotool_window_exists(win, env=env)):
        win = _safe_get_win(pid, strat, env=env)
        if not win:
            return None

    cp = _run_cmd([XDOTOOL, "key", "--window", win, "--clearmodifiers", key], env=env, timeout=2)
    if cp.returncode == 0:
        return win

    # retry once with re-search (window may have died)
    win2 = _safe_get_win(pid, strat, env=env)
    if not win2:
        return win
    _run_cmd([XDOTOOL, "key", "--window", win2, "--clearmodifiers", key], env=env, timeout=2)
    return win2


def _xdotool_get_geometry(win: str, *, env: dict) -> Optional[Tuple[int, int, int, int]]:
    """
    Returns (x, y, w, h) from:
    xdotool getwindowgeometry --shell <win>
    """
    cp = _run_cmd([XDOTOOL, "getwindowgeometry", "--shell", win], env=env, timeout=3)
    if cp.returncode != 0:
        return None

    # Example output:
    # X=123
    # Y=45
    # WIDTH=800
    # HEIGHT=600
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
        return None
    return x, y, w, h


def _capture_root_crop(win: str, out_png: Path, *, env: dict, trim: bool = True):
    """
    Stable capture:
      import -window root root.png
      convert root.png -crop WxH+X+Y (+trim) out.png
    """
    g = _xdotool_get_geometry(win, env=env)
    if not g:
        raise RuntimeError("Cannot get window geometry")
    x, y, w, h = g
    if w <= 0 or h <= 0:
        raise RuntimeError(f"Bad geometry: {g}")

    root_png = out_png.with_suffix(".root.png")

    cp1 = _run_cmd([IMPORT, "-window", "root", str(root_png)], env=env, timeout=15)
    if cp1.returncode != 0:
        raise RuntimeError(f"import root failed: {cp1.stderr.strip()[:400]}")

    crop_arg = f"{w}x{h}+{x}+{y}"
    cmd = [CONVERT, str(root_png), "-crop", crop_arg, "+repage"]
    if trim:
        cmd += ["-trim", "+repage"]
    cmd += [str(out_png)]

    cp2 = _run_cmd(cmd, env=env, timeout=20)
    if cp2.returncode != 0:
        raise RuntimeError(f"convert crop failed: {cp2.stderr.strip()[:400]}")


def _compile_pascal(source: str, workdir: Path) -> Path:
    """
    Compile PascalABC.NET source into .exe (or .dll depending on pabcnetc).
    We assume pabcnetc.exe is present and usable.
    """
    src = workdir / "main.pas"
    src.write_text(source, encoding="utf-8")

    # Typical CLI: pabcnetc.exe main.pas
    # Output usually main.exe in same folder.
    cp = _run_cmd([PABCNETC, str(src)], env=_env_base(), timeout=60, cwd=str(workdir))
    if cp.returncode != 0:
        msg = (cp.stdout or "") + "\n" + (cp.stderr or "")
        raise HTTPException(status_code=400, detail=f"Compile failed:\n{msg[-2000:]}")

    exe = workdir / "main.exe"
    if not exe.exists():
        # Some builds output .dll; try detect
        dll = workdir / "main.dll"
        if dll.exists():
            return dll
        # fallback: any exe in dir
        exes = list(workdir.glob("*.exe"))
        if exes:
            return exes[0]
        raise HTTPException(status_code=500, detail="Compile succeeded but output not found (no main.exe/main.dll).")
    return exe


def _run_program_capture(exe_path: Path, timeout_seconds: int, debug: bool) -> bytes:
    """
    Run compiled program under X (DrawMan/GraphABC).
    For DrawMan we:
      - start process
      - find window by pid + regex
      - focus+click
      - press Enter ONCE (no Space spam)
      - wait
      - capture root and crop to window
    """
    env = _env_base()
    strat = UiStrategy()

    with tempfile.TemporaryDirectory(prefix="tf_out_") as td:
        td_path = Path(td)
        out_png = td_path / "out.png"

        # Run program
        # If it's a dll: mono main.dll, if exe: mono main.exe
        cmd = [MONO, str(exe_path)]
        p = subprocess.Popen(
            cmd,
            env=env,
            cwd=str(exe_path.parent),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

        try:
            win = _safe_get_win(p.pid, strat, env=env)
            if not win:
                # dump stderr for debugging
                try:
                    _, err = p.communicate(timeout=1)
                except Exception:
                    err = ""
                raise HTTPException(status_code=500, detail=f"DrawMan window not found (pid={p.pid}).\n{err[-2000:]}")

            _xdotool_focus(win, env=env)
            _xdotool_click_center(win, env=env)

            # IMPORTANT: DO NOT PRESS SPACE (step) – it causes Thread.Resume crashes when thread ended.
            win = _safe_key(p.pid, win, "Return", strat, env=env)
            if not win:
                raise HTTPException(status_code=500, detail="Failed to send Enter to DrawMan window (window missing).")

            time.sleep(strat.draw_wait_seconds)

            # Stable capture
            try:
                _capture_root_crop(win, out_png, env=env, trim=True)
            except Exception as e:
                # last resort: attempt capture without trim
                try:
                    _capture_root_crop(win, out_png, env=env, trim=False)
                except Exception:
                    raise HTTPException(status_code=500, detail=f"Capture failed: {e}")

            if not out_png.exists():
                raise HTTPException(status_code=500, detail="Capture produced no output file.")

            data = out_png.read_bytes()
            if len(data) < 300:  # guard against empty PNG stubs
                raise HTTPException(status_code=500, detail=f"Capture PNG too small ({len(data)} bytes). Likely blank/failed.")

            return data

        finally:
            # stop process (avoid zombies)
            try:
                p.terminate()
            except Exception:
                pass
            try:
                p.wait(timeout=2)
            except Exception:
                try:
                    p.kill()
                except Exception:
                    pass


# -------------------------
# API
# -------------------------
@app.post("/render")
def render(req: RenderRequest):
    _require_tools()

    with tempfile.TemporaryDirectory(prefix="tf_work_") as td:
        wd = Path(td)
        exe = _compile_pascal(req.source, wd)
        png_bytes = _run_program_capture(exe, req.timeout_seconds, req.debug)

    return Response(content=png_bytes, media_type="image/png")
