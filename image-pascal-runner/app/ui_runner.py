import os
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Optional

# GraphABC only runner: start Xvfb, run program, screenshot root, trim.

DISPLAY = os.getenv("DISPLAY", ":99")
RUN_LANG = os.getenv("TF_LANG", "C.UTF-8")
RUN_LC_ALL = os.getenv("TF_LC_ALL", "C.UTF-8")

MONO = os.getenv("TF_MONO", "mono")
XVFB = os.getenv("TF_XVFB", "Xvfb")
IMPORT = os.getenv("TF_IMPORT", "import")      # ImageMagick
CONVERT = os.getenv("TF_CONVERT", "convert")   # ImageMagick

# How long to wait for GraphABC window to appear / finish drawing
GRAPHABC_WAIT_SECONDS = float(os.getenv("TF_GRAPHABC_WAIT", "0.8"))


def _require_tools():
    missing = []
    for t in [XVFB, IMPORT, CONVERT, MONO]:
        if shutil.which(t) is None:
            missing.append(t)
    if missing:
        raise RuntimeError(f"Missing tools in container: {', '.join(missing)}")


def _env(display: str) -> dict:
    e = os.environ.copy()
    e["DISPLAY"] = display
    e["LANG"] = RUN_LANG
    e["LC_ALL"] = RUN_LC_ALL
    return e


def _run(cmd, *, env=None, cwd: Optional[str] = None, timeout: int = 20) -> subprocess.CompletedProcess:
    return subprocess.run(
        cmd,
        env=env,
        cwd=cwd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        timeout=timeout,
    )


def _capture_root(out_png: Path, *, env: dict, trim: bool):
    # 1) full root screenshot
    cp1 = _run([IMPORT, "-window", "root", str(out_png)], env=env, timeout=30)
    if cp1.returncode != 0:
        raise RuntimeError(f"import failed: {(cp1.stdout or '').strip()[:400]}")

    if trim:
        tmp = out_png.with_suffix(".trim.png")
        cp2 = _run([CONVERT, str(out_png), "-trim", "+repage", str(tmp)], env=env, timeout=30)
        if cp2.returncode != 0:
            raise RuntimeError(f"convert trim failed: {(cp2.stdout or '').strip()[:400]}")
        tmp.replace(out_png)


def run_ui_and_capture(
    *,
    exe_path: Path,
    out_png: Path,
    xvfb_screen: str,
    timeout_seconds: int,
    trim: bool,
):
    """
    GraphABC only:
      - start Xvfb on DISPLAY (default :99)
      - run 'mono <exe>'
      - wait a bit for UI to render
      - capture screenshot of root, optionally trim
      - stop program and Xvfb
    """
    _require_tools()

    # Start Xvfb
    xvfb = subprocess.Popen(
        [XVFB, DISPLAY, "-screen", "0", xvfb_screen, "-ac"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        env=_env(DISPLAY),
    )

    env = _env(DISPLAY)

    try:
        # Small delay so Xvfb is ready
        time.sleep(0.15)

        p = subprocess.Popen(
            [MONO, str(exe_path)],
            cwd=str(exe_path.parent),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            env=env,
        )

        try:
            # Wait for UI to paint
            time.sleep(GRAPHABC_WAIT_SECONDS)

            # Screenshot
            _capture_root(out_png, env=env, trim=trim)

            if not out_png.exists() or out_png.stat().st_size < 300:
                # If program crashed early, show tail of stdout
                try:
                    out = (p.stdout.read() if p.stdout else "") or ""
                except Exception:
                    out = ""
                raise RuntimeError(f"capture png too small/empty. program_out_tail:\n{out[-2000:]}")

        finally:
            # Stop program
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

    finally:
        # Stop Xvfb
        try:
            xvfb.terminate()
        except Exception:
            pass
        try:
            xvfb.wait(timeout=2)
        except Exception:
            try:
                xvfb.kill()
            except Exception:
                pass
