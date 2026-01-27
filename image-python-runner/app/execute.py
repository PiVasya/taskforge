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


def _safe_int(v, default: int) -> int:
    try:
        x = int(float(v))
        return x if x > 0 else default
    except Exception:
        return default


def _parse_ps_bounding_box(ps_path: Path) -> tuple[int, int] | None:
    """
    Parse %%BoundingBox: llx lly urx ury from PS/EPS.
    Returns (width_points, height_points) in PostScript points (1/72 inch) or None.
    """
    try:
        with ps_path.open("r", encoding="utf-8", errors="ignore") as f:
            for _ in range(200):  # header should be early
                line = f.readline()
                if not line:
                    break
                if line.startswith("%%BoundingBox:"):
                    parts = line.strip().split()
                    # format: %%BoundingBox: llx lly urx ury
                    if len(parts) >= 5:
                        llx = _safe_int(parts[1], 0)
                        lly = _safe_int(parts[2], 0)
                        urx = _safe_int(parts[3], 0)
                        ury = _safe_int(parts[4], 0)
                        w = max(0, urx - llx)
                        h = max(0, ury - lly)
                        if w > 0 and h > 0:
                            return (w, h)
                    break
    except Exception as e:
        log(f"WARNING: failed to parse BoundingBox: {e}")
    return None


def _normalize_bg_color(bg) -> tuple[int, int, int]:
    """
    Turtle screen.bgcolor() may return:
      - a color name string ("navy")
      - a hex string ("#112233")
      - an (r,g,b) tuple in 0..1 floats OR 0..255 ints
    Convert to (R,G,B) ints 0..255.
    """
    # Import here to avoid dependency if never used
    from PIL import ImageColor

    if bg is None:
        return (255, 255, 255)

    if isinstance(bg, str):
        try:
            return ImageColor.getrgb(bg)
        except Exception:
            return (255, 255, 255)

    if isinstance(bg, (tuple, list)) and len(bg) >= 3:
        r, g, b = bg[0], bg[1], bg[2]
        try:
            rf = float(r)
            gf = float(g)
            bf = float(b)
            # 0..1 floats
            if 0.0 <= rf <= 1.0 and 0.0 <= gf <= 1.0 and 0.0 <= bf <= 1.0:
                return (int(rf * 255), int(gf * 255), int(bf * 255))
            # assume 0..255
            return (int(rf), int(gf), int(bf))
        except Exception:
            return (255, 255, 255)

    return (255, 255, 255)


def _bake_background_if_needed(out_png: Path, bg_color, target_size: tuple[int, int] | None) -> None:
    """
    If PNG has alpha, composite onto background color to avoid transparent background in UI.
    Also optionally resize to exact target_size.
    """
    keep_alpha = os.getenv("TF_KEEP_ALPHA", "").strip() in ("1", "true", "True", "YES", "yes")
    if keep_alpha:
        log("TF_KEEP_ALPHA=1 -> keep transparency (skip background bake)")
        return

    from PIL import Image

    img = Image.open(out_png)
    try:
        # Resize first (keeps crispness more consistent after crop)
        if target_size and img.size != target_size:
            log(f"Resizing PNG from {img.size} -> {target_size}")
            img = img.resize(target_size, Image.Resampling.LANCZOS)

        if img.mode in ("RGBA", "LA") or ("A" in img.getbands()):
            rgb = _normalize_bg_color(bg_color)
            log(f"Baking background color {bg_color} -> RGB{rgb}")
            base = Image.new("RGBA", img.size, rgb + (255,))
            # Ensure RGBA for alpha_composite
            img_rgba = img.convert("RGBA")
            out = Image.alpha_composite(base, img_rgba).convert("RGB")
            out.save(out_png, format="PNG", optimize=True)
            log("Background baked (saved RGB PNG, no alpha).")
        else:
            # No alpha; only resize may have happened
            if target_size and img.size == target_size:
                img.save(out_png, format="PNG", optimize=True)
                log("Saved PNG after resize (no alpha).")
    finally:
        try:
            img.close()
        except Exception:
            pass


def _try_capture_turtle_postscript(ps_path: Path) -> tuple[int, int, object]:
    """Try to capture turtle canvas into PostScript. Returns (canvas_w_px, canvas_h_px, bgcolor)."""
    import turtle as t

    log("Capturing turtle canvas -> PostScript")
    # Force pending drawing operations.
    bg = None
    try:
        scr = t.Screen()
        try:
            bg = scr.bgcolor()
        except Exception:
            bg = None
        # Some code disables tracer; update() forces drawing.
        scr.update()
    except Exception as e:
        log(f"Screen() / update() failed (still trying capture): {e}")

    canvas = t.getcanvas()
    ps_path.parent.mkdir(parents=True, exist_ok=True)

    # Try to get actual canvas dimensions
    try:
        canvas.update_idletasks()
    except Exception:
        pass

    cw = _safe_int(getattr(canvas, "winfo_width", lambda: 0)(), 0)
    ch = _safe_int(getattr(canvas, "winfo_height", lambda: 0)(), 0)

    # cget('width')/'height' often more stable in headless
    try:
        cw_opt = _safe_int(canvas.cget("width"), 0)
        ch_opt = _safe_int(canvas.cget("height"), 0)
        if cw_opt > 0 and ch_opt > 0:
            cw, ch = cw_opt, ch_opt
    except Exception:
        pass

    # Final fallback
    if cw <= 1:
        cw = 800
    if ch <= 1:
        ch = 600

    log(f"Canvas size detected: {cw}x{ch}px (bg={bg})")

    # Export EXACT region of the canvas to avoid A4/Letter "page" padding/shrinking.
    canvas.postscript(
        file=str(ps_path),
        colormode="color",
        x=0,
        y=0,
        width=cw,
        height=ch,
    )
    log(f"PostScript saved: {ps_path}")
    return cw, ch, bg


def _convert_ps_to_png(ps_path: Path, out_png: Path, target_px: tuple[int, int] | None) -> None:
    """Convert PostScript to PNG using Ghostscript (more reliable in Docker)."""
    log("Converting PostScript -> PNG (ghostscript)")
    out_png.parent.mkdir(parents=True, exist_ok=True)

    # Compute DPI from BoundingBox so the raster result matches canvas px closely.
    dpi = 144.0
    bbox = _parse_ps_bounding_box(ps_path)
    if bbox and target_px:
        w_pt, h_pt = bbox
        tw, th = target_px
        # points -> inches: pt/72 ; pixels = inches * dpi  => dpi = pixels * 72 / pt
        try:
            dpi_x = (tw * 72.0) / float(w_pt)
            dpi_y = (th * 72.0) / float(h_pt)
            # If aspect ratio matches, dpi_x≈dpi_y. Use average, clamp sane range.
            dpi = max(36.0, min(600.0, (dpi_x + dpi_y) / 2.0))
            log(f"BoundingBox pt={w_pt}x{h_pt}, target px={tw}x{th} -> dpi≈{dpi:.2f}")
        except Exception as e:
            log(f"WARNING: DPI calc failed: {e} (using default 144)")

    # -dEPSCrop is critical: it crops to BoundingBox to avoid huge page and "shrink" effect.
    cmd = [
        "gs",
        "-dSAFER",
        "-dBATCH",
        "-dNOPAUSE",
        "-dEPSCrop",
        "-sDEVICE=pngalpha",
        f"-r{dpi:.2f}",
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
    ps_path = out_png.with_suffix(".ps")

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
        cw, ch, bg = _try_capture_turtle_postscript(ps_path)
        target_px = (cw, ch)
    except Exception:
        log("ERROR: failed to capture turtle canvas")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_CAPTURE_PS")

    # Convert.
    try:
        _convert_ps_to_png(ps_path, out_png, target_px=target_px)
    except Exception:
        log("ERROR: failed to convert PS -> PNG")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_CONVERT_PNG")

    # Bake background (fix "bgcolor lost") + ensure exact size.
    try:
        _bake_background_if_needed(out_png, bg_color=bg, target_size=target_px)
    except Exception:
        log("WARNING: background bake failed (keeping original PNG)")
        print(traceback.format_exc(), flush=True)

    _dump_dir(out_png.parent, "AFTER_BAKE_BG")

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
