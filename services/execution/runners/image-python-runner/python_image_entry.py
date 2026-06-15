import os
import runpy
import sys
import time

os.environ.setdefault("MPLBACKEND", "Agg")
os.environ.setdefault("MPLCONFIGDIR", "/tmp/matplotlib")
os.environ.setdefault("PYTHONPYCACHEPREFIX", "/tmp/pycache")


def _patch_turtle_for_autocheck() -> None:
    """Make school-style turtle.done()/mainloop()/exitonclick() safe for headless autocheck.

    Normal Python turtle code often ends with turtle.done(), which starts Tk's
    blocking event loop. In an online judge that means a false timeout. Legacy
    TaskForge patched this behavior, so we keep the same idea here: refresh the
    canvas briefly so Xvfb screenshot capture can see the final drawing, then
    return instead of blocking forever.
    """
    try:
        import turtle
    except Exception:
        return

    def _flush_canvas(*_args, **_kwargs):
        try:
            screen = turtle.Screen()
            try:
                screen.update()
            except Exception:
                pass
            # Give the Go runner one or two capture ticks while the Tk window is
            # still alive. Keep it short so tests do not hang.
            time.sleep(float(os.getenv("TASKFORGE_TURTLE_DONE_DELAY", "0.7")))
            try:
                screen.update()
            except Exception:
                pass
        except Exception:
            pass
        return None

    for name in ("done", "mainloop", "exitonclick"):
        try:
            setattr(turtle, name, _flush_canvas)
        except Exception:
            pass
    try:
        turtle.Screen().mainloop = _flush_canvas
    except Exception:
        pass


_patch_turtle_for_autocheck()

if len(sys.argv) < 2:
    print("Usage: python_image_entry.py main.py", file=sys.stderr)
    raise SystemExit(2)

script = sys.argv[1]
sys.argv = [script] + sys.argv[2:]
runpy.run_path(script, run_name="__main__")

# Quality-of-life for school matplotlib tasks: if the student created a figure
# but forgot savefig('out.png'), save the current figure automatically.
try:
    import matplotlib.pyplot as plt
    if plt.get_fignums() and not any(os.path.exists(name) for name in ("out.png", "out.jpg", "out.jpeg", "out.ppm", "out.bmp")):
        plt.savefig("out.png", bbox_inches="tight")
except Exception as exc:
    print(f"[taskforge-python-image] auto-save skipped: {exc}", file=sys.stderr)
