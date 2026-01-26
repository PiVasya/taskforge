import os
import sys
import time
import runpy
import traceback
import subprocess
import re
import shutil
from pathlib import Path

from PIL import Image, ImageColor
import ast

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
    w = int(canvas.winfo_width() or 0)
    h = int(canvas.winfo_height() or 0)
    log(f"Canvas size: {w}x{h}")
    canvas.postscript(file=str(ps_path), colormode='color', x=0, y=0, width=w, height=h)
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
        "-dEPSCrop",
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



def _resolve_bgcolor_from_turtle() -> str | None:
    """Best effort: read current turtle screen background color."""
    try:
        import turtle as t
        scr = t.Screen()
        c = scr.bgcolor()  # when called without args returns current background
        if isinstance(c, tuple) and len(c) >= 3:
            r, g, b = c[:3]
            # turtle sometimes returns 0..1 floats
            if all(isinstance(x, float) for x in (r, g, b)):
                return f"#{int(r*255):02x}{int(g*255):02x}{int(b*255):02x}"
            return f"#{int(r):02x}{int(g):02x}{int(b):02x}"
        if isinstance(c, str) and c.strip():
            return c.strip()
    except Exception as e:
        log(f"bgcolor read from turtle failed: {e}")
    return None


def _resolve_bgcolor_from_source(user_path: Path) -> str | None:
    """Fallback: parse screen.bgcolor(...) from user's source (string/tuple literals only)."""
    try:
        txt = user_path.read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return None

    # string literal: bgcolor("navy") / bgcolor('#001122')
    m = re.search(r"\.bgcolor\(\s*(['\"])(.*?)\1\s*\)", txt)
    if m:
        return m.group(2).strip()

    # tuple literal: bgcolor((r,g,b)) or bgcolor(r,g,b)
    m = re.search(r"\.bgcolor\(\s*(\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\)|\d+\s*,\s*\d+\s*,\s*\d+)\s*\)", txt)
    if m:
        expr = m.group(1)
        try:
            val = ast.literal_eval(expr) if expr.strip().startswith("(") else tuple(int(x.strip()) for x in expr.split(","))
            if isinstance(val, tuple) and len(val) == 3:
                r, g, b = val
                return f"#{int(r):02x}{int(g):02x}{int(b):02x}"
        except Exception:
            pass
    return None


def _apply_background_to_png(out_png: Path, bgcolor: str | None) -> None:
    """If PNG has transparency, composite it onto bgcolor and save as RGB PNG."""
    if not out_png.exists() or out_png.stat().st_size == 0:
        return

    bg = (255, 255, 255)
    if bgcolor:
        try:
            bg = ImageColor.getrgb(bgcolor)
        except Exception as e:
            log(f"bgcolor '{bgcolor}' is not understood by Pillow: {e}. Using white.")
            bg = (255, 255, 255)

    with Image.open(out_png) as im:
        # Normalize to RGBA to access alpha
        if im.mode not in ("RGBA", "LA"):
            im = im.convert("RGBA")
        else:
            im = im.copy()

        alpha = im.split()[-1]
        # If alpha is fully opaque - still convert to RGB to remove accidental alpha channel.
        bg_im = Image.new("RGB", im.size, bg)
        bg_im.paste(im, mask=alpha)
        bg_im.save(out_png, format="PNG", optimize=True)
        log(f"Applied background {bg} to PNG and saved RGB: {out_png}")


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

# If output has transparency, composite onto student's turtle Screen().bgcolor()
try:
    bg = _resolve_bgcolor_from_turtle() or _resolve_bgcolor_from_source(user_path)
    log(f"Resolved turtle bgcolor: {bg}")
    _apply_background_to_png(out_png, bg)
except Exception as e:
    log(f"WARNING: applying background failed: {e}")

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
