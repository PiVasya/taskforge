import os
import sys
import time
import runpy
import traceback
import subprocess
import shutil
from pathlib import Path


def log(msg: str) -> None:
    # Simple structured-ish logs; easy to grep in Actions.
    ts = time.strftime('%Y-%m-%d %H:%M:%S')
    print(f"[{ts}] [python-image-runner] {msg}", flush=True)


def _dump_dir(p: Path, title: str) -> None:
    """Best-effort directory dump to help debug 'no image produced' cases."""
    try:
        log(f"{title}: listing dir {p}")
        if not p.exists():
            log(f"{title}: dir does not exist")
            return
        for it in sorted(p.iterdir(), key=lambda x: (not x.is_dir(), x.name.lower())):
            try:
                if it.is_dir():
                    log(f"  [DIR ] {it.name}")
                else:
                    log(f"  [FILE] {it.name} size={it.stat().st_size}")
            except Exception as e:
                log(f"  [????] {it.name} stat failed: {e}")
    except Exception as e:
        log(f"{title}: dump failed: {e}")


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

    _dump_dir(out_png.parent, "INIT")
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
        log(f"cwd={os.getcwd()}")
        log(f"env DISPLAY={os.environ.get('DISPLAY')} PYTHONPATH={os.environ.get('PYTHONPATH')}")
        runpy.run_path(str(user_path), run_name="__main__")
        log("User script finished")
    except SystemExit as e:
        # Some scripts call exit(); treat as normal.
        log(f"User script SystemExit: {e}")
    except Exception:
        log("ERROR: exception while executing user script")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_RUN")

    # ✅ If user produced the PNG directly (PIL/matplotlib/etc), don't touch turtle.
    try:
        if out_png.exists() and out_png.stat().st_size > 0:
            log(f"User produced PNG directly: {out_png} bytes={out_png.stat().st_size} -> skip turtle capture")
            return 0
        log("No direct out.png in workdir (or empty). Will try fallback paths / turtle.")
    except Exception as e:
        log(f"WARNING: checking out_png failed: {e} (continuing)")

    # ✅ Backward-compat: if someone still writes /tmp/out.png, copy it.
    tmp_out = Path("/tmp/out.png")
    try:
        if tmp_out.exists() and tmp_out.stat().st_size > 0:
            log(f"Found /tmp/out.png bytes={tmp_out.stat().st_size}. Copying -> {out_png}")
            shutil.copyfile(tmp_out, out_png)
            log(f"Copied OK. bytes={out_png.stat().st_size} -> skip turtle capture")
            return 0
        log("No /tmp/out.png (or empty).")

    except Exception as e:
        log(f"WARNING: checking/copying /tmp/out.png failed: {e} (continuing)")

    # Give tkinter a tiny bit of time to paint (only needed for turtle).
    time.sleep(float(os.getenv("POST_RUN_SLEEP_SECONDS", "0.2")))

    # Capture.
    try:
        _try_capture_turtle_postscript(ps_path)
    except Exception:
        log("ERROR: failed to capture turtle canvas")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_CAPTURE_PS")

    # Convert.
    try:
        _convert_ps_to_png(ps_path, out_png)
    except Exception:
        log("ERROR: failed to convert PS -> PNG")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_CONVERT_PNG")

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
