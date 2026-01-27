import os
import sys
import time
import runpy
import traceback
import subprocess
import shutil
from pathlib import Path


def log(msg: str) -> None:
    ts = time.strftime('%Y-%m-%d %H:%M:%S')
    print(f"[{ts}] [python-image-runner] {msg}", flush=True)


def _dump_dir(p: Path, title: str) -> None:
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


def _normalize_bg_color(bg) -> tuple[int, int, int]:
    """
    turtle.bgcolor() may return:
      - color name string ("navy")
      - hex string ("#112233")
      - (r,g,b) floats 0..1 or ints 0..255
    convert -> (R,G,B) ints 0..255
    """
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
            rf, gf, bf = float(r), float(g), float(b)
            if 0.0 <= rf <= 1.0 and 0.0 <= gf <= 1.0 and 0.0 <= bf <= 1.0:
                return (int(rf * 255), int(gf * 255), int(bf * 255))
            return (int(rf), int(gf), int(bf))
        except Exception:
            return (255, 255, 255)

    return (255, 255, 255)


def _bake_background_if_needed(out_png: Path, bg_color, target_size: tuple[int, int]) -> None:
    """
    Bake transparency -> RGB background.
    Also enforce exact output size == target_size.
    Set TF_KEEP_ALPHA=1 to keep alpha.
    """
    keep_alpha = os.getenv("TF_KEEP_ALPHA", "").strip() in ("1", "true", "True", "YES", "yes")
    if keep_alpha:
        log("TF_KEEP_ALPHA=1 -> keep transparency (skip background bake)")
        return

    from PIL import Image

    img = Image.open(out_png)
    try:
        # Enforce size (важно для стабильного сравнения и чтобы UI совпадал)
        if img.size != target_size:
            log(f"Resizing PNG from {img.size} -> {target_size}")
            img = img.resize(target_size, Image.Resampling.LANCZOS)

        # If alpha present -> composite onto bg
        if img.mode in ("RGBA", "LA") or ("A" in img.getbands()):
            rgb = _normalize_bg_color(bg_color)
            log(f"Baking background: bgcolor={bg_color} -> RGB{rgb}")
            base = Image.new("RGBA", img.size, rgb + (255,))
            out = Image.alpha_composite(base, img.convert("RGBA")).convert("RGB")
            out.save(out_png, format="PNG", optimize=True)
            log("Background baked (saved RGB PNG).")
        else:
            # no alpha, just save after resize
            img.save(out_png, format="PNG", optimize=True)
            log("Saved PNG (no alpha).")
    finally:
        try:
            img.close()
        except Exception:
            pass


def _capture_turtle_ps(ps_path: Path) -> tuple[int, int, object]:
    """
    Key fix:
    - make scrollregion == window size (screensize = window size)
    - export PS for region x=-W/2,y=-H/2,width=W,height=H
      (turtle coords are centered -> this grabs full visible area)
    - use pagewidth/pageheight to avoid Tk 0.75 scaling
    """
    import turtle as t

    log("Capturing turtle canvas -> PostScript")

    # Get screen, background
    scr = None
    bg = None
    try:
        scr = t.Screen()
        try:
            bg = scr.bgcolor()
        except Exception:
            bg = None
        # ensure draw flush
        try:
            scr.update()
        except Exception:
            pass
    except Exception as e:
        log(f"Screen() failed: {e}")

    canvas = t.getcanvas()
    ps_path.parent.mkdir(parents=True, exist_ok=True)

    # window size (what student expects after screen.setup)
    try:
        w_win = _safe_int(canvas.winfo_width(), 0)
        h_win = _safe_int(canvas.winfo_height(), 0)
    except Exception:
        w_win, h_win = 0, 0

    # fallback from canvas options
    try:
        w_opt = _safe_int(canvas.cget("width"), 0)
        h_opt = _safe_int(canvas.cget("height"), 0)
        if w_win <= 1 and w_opt > 1:
            w_win = w_opt
        if h_win <= 1 and h_opt > 1:
            h_win = h_opt
    except Exception:
        pass

    if w_win <= 1:
        w_win = 800
    if h_win <= 1:
        h_win = 600

    log(f"Window/canvas size: {w_win}x{h_win}px, bg={bg}")

    # 🔥 CRITICAL: make scrollregion match window size so center (0,0) maps correctly
    # screensize sets scrollregion to [-W/2..W/2, -H/2..H/2]
    try:
        if scr is not None:
            scr.screensize(w_win, h_win)
            try:
                scr.update()
            except Exception:
                pass
            log(f"Applied screensize({w_win},{h_win}) to fix scrollregion")
    except Exception as e:
        log(f"WARNING: screensize failed: {e}")

    # Export full visible turtle world:
    # x,y are in canvas coordinates which are centered due to scrollregion.
    x0 = -w_win // 2
    y0 = -h_win // 2

    # IMPORTANT: pagewidth/pageheight remove Tk's internal 0.75 scaling (96->72dpi issue)
    # Many setups work best with -1 to avoid off-by-one rounding artifacts.
    page_w = max(1, w_win - 1)
    page_h = max(1, h_win - 1)

    log(f"postscript region: x={x0} y={y0} w={w_win} h={h_win} page={page_w}x{page_h}")

    canvas.postscript(
        file=str(ps_path),
        colormode="color",
        x=x0,
        y=y0,
        width=w_win,
        height=h_win,
        pagewidth=page_w,
        pageheight=page_h,
        pageanchor="nw",
    )

    log(f"PostScript saved: {ps_path}")
    return w_win, h_win, bg


def _convert_ps_to_png(ps_path: Path, out_png: Path) -> None:
    log("Converting PostScript -> PNG (ghostscript)")
    out_png.parent.mkdir(parents=True, exist_ok=True)

    # 144 gives good edges; final size is enforced later anyway.
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

    # Patch blocking loop BEFORE user code run
    log("Patching turtle.done/mainloop to be non-blocking")
    try:
        import turtle as t

        def _no_block(*_a, **_kw):
            log("turtle.done/mainloop called -> ignored (non-blocking runner)")
            return None

        t.done = _no_block  # type: ignore[attr-defined]
        t.mainloop = _no_block  # type: ignore[attr-defined]
        try:
            from turtle import Screen
            Screen.mainloop = _no_block  # type: ignore[method-assign]
        except Exception:
            pass
    except Exception as e:
        log(f"WARNING: turtle patch failed: {e}")

    # Run user code
    log("Executing user script")
    try:
        log(f"cwd={os.getcwd()}")
        log(f"env DISPLAY={os.environ.get('DISPLAY')} PYTHONPATH={os.environ.get('PYTHONPATH')}")
        runpy.run_path(str(user_path), run_name="__main__")
        log("User script finished")
    except SystemExit as e:
        log(f"User script SystemExit: {e}")
    except Exception:
        log("ERROR: exception while executing user script")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_RUN")

    # If user produced PNG directly -> done
    try:
        if out_png.exists() and out_png.stat().st_size > 0:
            log(f"User produced PNG directly: {out_png} bytes={out_png.stat().st_size} -> skip turtle capture")
            return 0
        log("No direct out.png in workdir (or empty). Will try fallback paths / turtle.")
    except Exception as e:
        log(f"WARNING: checking out_png failed: {e} (continuing)")

    # Backward compat: /tmp/out.png
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

    # Give Tk tiny time
    time.sleep(float(os.getenv("POST_RUN_SLEEP_SECONDS", "0.2")))

    # Capture PS (fix shift/crop here)
    try:
        w, h, bg = _capture_turtle_ps(ps_path)
    except Exception:
        log("ERROR: failed to capture turtle canvas")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_CAPTURE_PS")

    # Convert PS -> PNG
    try:
        _convert_ps_to_png(ps_path, out_png)
    except Exception:
        log("ERROR: failed to convert PS -> PNG")
        print(traceback.format_exc(), flush=True)
        return 1

    _dump_dir(out_png.parent, "AFTER_CONVERT_PNG")

    # Bake background and enforce exact final size
    try:
        _bake_background_if_needed(out_png, bg_color=bg, target_size=(w, h))
    except Exception:
        log("WARNING: background bake failed (keeping original PNG)")
        print(traceback.format_exc(), flush=True)

    _dump_dir(out_png.parent, "AFTER_BAKE_BG")

    # Close turtle
    try:
        import turtle as t
        t.bye()
        log("turtle.bye() called")
    except Exception as e:
        log(f"WARNING: turtle.bye() failed: {e}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
