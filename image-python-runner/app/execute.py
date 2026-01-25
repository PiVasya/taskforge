"""Isolated execution entrypoint.

Runs user code with runpy and attempts to produce a PNG image.

Output contract:
  - If user created OUT_PNG already -> keep.
  - Else try turtle canvas capture -> OUT_PNG.
"""

import runpy
import sys
from pathlib import Path


def _try_capture_turtle(out_png: Path) -> bool:
    try:
        import turtle  # noqa
        from PIL import Image

        # If turtle wasn't used, Screen/canvas may not exist.
        try:
            canvas = turtle.getcanvas()
        except Exception:
            return False

        # Dump PostScript.
        ps_path = out_png.with_suffix(".ps")
        try:
            canvas.postscript(file=str(ps_path), colormode="color")
        except Exception:
            return False

        # Convert PS->PNG via Pillow (uses Ghostscript).
        try:
            img = Image.open(ps_path)
            img.load()
            img.save(out_png, format="PNG")
        except Exception:
            return False
        finally:
            try:
                ps_path.unlink(missing_ok=True)
            except Exception:
                pass
        return out_png.exists() and out_png.stat().st_size > 0
    except Exception:
        return False


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: execute.py <user.py> <out.png>", file=sys.stderr)
        return 2

    user = Path(sys.argv[1]).resolve()
    out_png = Path(sys.argv[2]).resolve()

    # Ensure cwd doesn't pollute.
    try:
        runpy.run_path(str(user), run_name="__main__")
    except SystemExit as e:
        # allow scripts that call sys.exit(0)
        code = int(getattr(e, "code", 0) or 0)
        if code != 0:
            raise
    
    if out_png.exists() and out_png.stat().st_size > 0:
        return 0

    if _try_capture_turtle(out_png):
        return 0

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
