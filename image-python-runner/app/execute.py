import os
import sys
import time
import runpy
import traceback
import subprocess
from pathlib import Path


def log(msg: str) -> None:
    # Simple structured-ish logs; easy to grep in Actions.
    ts = time.strftime('%Y-%m-%d %H:%M:%S')
    print(f"[{ts}] [python-image-runner] {msg}", flush=True)


def _try_capture_turtle_postscript(ps_path: Path) -> None:
    """Try to capture turtle canvas into PostScript."""
    import turtle as t

    log("Capturing turtle canvas -> PostScript")
    # Force pending drawing operations.
    try:
        scr = t.Screen()
        # Some code disables tracer; update() forces drawing.
        scr.update()
    except Exception as e:
        log(f"Screen() / update() failed (still trying capture): {e}")

    # turtle.getcanvas() exists on tkinter backend.
    canvas = t.getcanvas()
    ps_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.postscript(file=str(ps_path), colormode='color')
    log(f"PostScript saved: {ps_path}")


def _convert_ps_to_png(ps_path: Path, out_png: Path) -> None:
    """Convert PostScript to PNG using Ghostscript (more reliable in Docker)."""
    log("Converting PostScript -> PNG (ghostscript)")
    out_png.parent.mkdir(parents=True, exist_ok=True)

    # 144 dpi gives decent quality without being huge.
    cmd = [
        "gs",
        "-dSAFER",
        "-dBATCH",
        "-dNOPAUSE",
        "-sDEVICE=pngalpha",
        "-r144",
        f"-sOutputFile={str(out_png)}",
        str(ps_path),
    ]
    log("gs cmd: " + " ".join(cmd))
    p = subprocess.run(cmd, capture_output=True, text=True)
    if p.stdout:
        log("gs stdout: " + p.stdout.strip())
    if p.stderr:
        log("gs stderr: " + p.stderr.strip())
    if p.returncode != 0:
        raise RuntimeError(f"ghostscript failed with exit code {p.returncode}")
    if not out_png.exists() or out_png.stat().st_size == 0:
        raise RuntimeError("ghostscript produced empty PNG")
    log(f"PNG saved: {out_png} ({out_png.stat().st_size} bytes)")


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: python -m app.execute <user.py> <out.png>")
        return 2

    user_path = Path(sys.argv[1]).resolve()
    out_png = Path(sys.argv[2]).resolve()
    ps_path = out_png.with_suffix('.ps')

    log(f"User file: {user_path}")
    log(f"Output PNG: {out_png}")

    if not user_path.exists():
        log("ERROR: user file not found")
        return 2

    # IMPORTANT:
    # Turtle scripts often end with turtle.done() which blocks forever.
    # We patch turtle.done/mainloop BEFORE running user's script.
    log("Patching turtle.done/mainloop to be non-blocking")
    try:
        import turtle as t

        def _no_block(*_a, **_kw):
            log("turtle.done/mainloop called -> ignored (non-blocking runner)")
            return None

        t.done = _no_block  # type: ignore[attr-defined]
        t.mainloop = _no_block  # type: ignore[attr-defined]
        try:
            # Sometimes user calls Screen().mainloop()
            from turtle import Screen

            Screen.mainloop = _no_block  # type: ignore[method-assign]
        except Exception:
            pass
    except Exception as e:
        log(f"WARNING: turtle patch failed: {e}")

    # Run user code. NOTE: this is NOT a security sandbox.
    # This runner is intended to run in an isolated container/network.
    log("Executing user script")
    try:
        runpy.run_path(str(user_path), run_name="__main__")
        log("User script finished")
    except SystemExit as e:
        # Some scripts call exit(); treat as normal.
        log(f"User script SystemExit: {e}")
    except Exception:
        log("ERROR: exception while executing user script")
        print(traceback.format_exc(), flush=True)
        return 1

    # Give tkinter a tiny bit of time to paint.
    time.sleep(float(os.getenv("POST_RUN_SLEEP_SECONDS", "0.2")))

    # Capture.
    try:
        _try_capture_turtle_postscript(ps_path)
    except Exception:
        log("ERROR: failed to capture turtle canvas")
        print(traceback.format_exc(), flush=True)
        return 1

    # Convert.
    try:
        _convert_ps_to_png(ps_path, out_png)
    except Exception:
        log("ERROR: failed to convert PS -> PNG")
        print(traceback.format_exc(), flush=True)
        return 1

    # Try to close turtle window cleanly.
    try:
        import turtle as t
        t.bye()
        log("turtle.bye() called")
    except Exception as e:
        log(f"WARNING: turtle.bye() failed: {e}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
