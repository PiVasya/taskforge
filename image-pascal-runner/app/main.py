import base64
import os
import re
import shutil
import subprocess
import tempfile
import time
import uuid

from fastapi import FastAPI
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel

app = FastAPI(title="image-pascal-runner")


class RenderRequest(BaseModel):
    code: str
    # Optional tuning (kept compatible with python runner shape)
    width: int | None = None
    height: int | None = None
    # debug flag isn't used here; use /render/debug endpoint instead


def _detect_drawing_kind(src: str) -> str | None:
    s = src.lower()
    # Most common PascalABC.NET drawing libs for school tasks
    if "drawman" in s or "drawman" in s or "drawman;" in s or "uses drawman" in s or "uses drawman" in s:
        return "drawman"
    if "graphabc" in s:
        return "graphabc"
    if re.search(r"\buses\s+turtle\b", s) or "turtle" in s:
        # PascalABC.NET turtle unit
        return "turtle"
    if "abcobjects" in s:
        return "abcobjects"
    if "graphwpf" in s:
        return "graphwpf"
    return None


def _validate_source(src: str) -> str | None:
    kind = _detect_drawing_kind(src)
    if kind is None:
        return (
            "В коде не найдено ни одного поддерживаемого графического модуля PascalABC.NET. "
            "Для проверки картинок используйте один из вариантов: uses GraphABC; или uses DrawMan; "
            "или uses Turtle; (также поддерживаются ABCObjects/GraphWPF)."
        )
    return None


def _run(cmd: list[str], timeout: int = 30) -> tuple[int, str, str]:
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        out, err = p.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        p.kill()
        out, err = p.communicate()
        return 124, out, (err + "\n[timeout]")
    return p.returncode, out, err


def _xvfb_script(exe_path: str, png_path: str, kind: str) -> str:
    # DrawMan часто стартует в режиме "ожидание Enter". Нажмём Enter автоматически.
    # Плюс подождём чуть дольше, чтобы успел дорисовать.
    press_enter = ""
    extra_sleep = "0.7"
    if kind == "drawman":
        press_enter = r"""
for i in $(seq 1 50); do
  # Try common window names first
  win=$(xdotool search --onlyvisible --name 'DrawMan' 2>/dev/null | head -n1 || true)
  if [ -z "$win" ]; then
    win=$(xdotool search --onlyvisible --name '.*Поле.*' 2>/dev/null | head -n1 || true)
  fi
  if [ -z "$win" ]; then
    win=$(xdotool search --onlyvisible --name '.*' 2>/dev/null | tail -n1 || true)
  fi
  if [ -n "$win" ]; then
    xdotool windowactivate "$win" 2>/dev/null || true
    xdotool key --window "$win" Return 2>/dev/null || xdotool key Return 2>/dev/null || true
    break
  fi
  sleep 0.1
done
"""
        extra_sleep = "1.2"

    return rf"""#!/usr/bin/env bash
set -e

mono "{exe_path}" > /tmp/app_stdout.txt 2> /tmp/app_stderr.txt &
APP_PID=$!

# Give GUI time to show
sleep 0.4

{press_enter}

sleep {extra_sleep}

# Screenshot whole virtual screen
import -window root "{png_path}" >/dev/null 2>&1 || true

# Cleanup process (don't hang container)
kill $APP_PID >/dev/null 2>&1 || true
wait $APP_PID >/dev/null 2>&1 || true
"""


def _render_impl(code: str) -> tuple[bytes | None, str, str, str | None]:
    """Returns: (png_bytes, stdout, stderr, error_message)"""

    err_msg = _validate_source(code)
    if err_msg:
        return None, "", "", err_msg

    kind = _detect_drawing_kind(code) or "unknown"

    workdir = tempfile.mkdtemp(prefix="pascal_render_")
    try:
        src_path = os.path.join(workdir, "main.pas")
        exe_path = os.path.join(workdir, "app.exe")
        png_path = os.path.join(workdir, f"out_{uuid.uuid4().hex}.png")

        with open(src_path, "w", encoding="utf-8") as f:
            f.write(code)

        # Compile
        # pabcnetc is installed in image at /opt/pabcnetc/pabcnetc.exe
        rc, out, err = _run(["mono", "/opt/pabcnetc/pabcnetc.exe", "/OutputDir:" + workdir, src_path], timeout=60)
        if rc != 0 or not os.path.exists(exe_path):
            msg = "Ошибка компиляции PascalABC.NET"
            return None, out, err, msg

        # Run inside Xvfb and take screenshot
        script_path = os.path.join(workdir, "run.sh")
        with open(script_path, "w", encoding="utf-8") as f:
            f.write(_xvfb_script(exe_path, png_path, kind))
        os.chmod(script_path, 0o755)

        rc2, out2, err2 = _run(["xvfb-run", "-a", "-s", "-screen 0 800x600x24", script_path], timeout=20)
        stdout = (out or "") + ("\n" + out2 if out2 else "")
        stderr = (err or "") + ("\n" + err2 if err2 else "")

        if not os.path.exists(png_path) or os.path.getsize(png_path) < 2000:
            # Usually means nothing got drawn or window didn't appear
            msg = "Не удалось получить изображение (окно не появилось или ничего не нарисовано)."
            return None, stdout, stderr, msg

        with open(png_path, "rb") as f:
            return f.read(), stdout, stderr, None

    finally:
        shutil.rmtree(workdir, ignore_errors=True)


@app.post("/render")
def render(req: RenderRequest):
    png, stdout, stderr, err = _render_impl(req.code)
    if err is not None:
        # Match python-runner style: { detail: { message, stdout, stderr } }
        raise Exception({"message": err, "stdout": stdout, "stderr": stderr})

    # Write to temp file for FileResponse
    tmp = tempfile.NamedTemporaryFile(delete=False, suffix=".png")
    try:
        tmp.write(png)
        tmp.flush()
        return FileResponse(tmp.name, media_type="image/png")
    finally:
        tmp.close()


@app.post("/render/debug")
def render_debug(req: RenderRequest):
    png, stdout, stderr, err = _render_impl(req.code)
    # Always 200 in debug mode so backend can show logs
    png_b64 = base64.b64encode(png).decode("ascii") if png else None
    return {
        "pngBase64": png_b64,
        "stdout": stdout,
        "stderr": (stderr + ("\n" + err if err else "")) if (stderr or err) else "",
    }


# Convert our internal Exception raised above into proper FastAPI HTTP error
@app.exception_handler(Exception)
async def _exception_handler(_, exc: Exception):
    # If we raised `Exception(dict)` above, it's stored in args[0]
    if exc.args and isinstance(exc.args[0], dict) and {"message", "stdout", "stderr"}.issubset(exc.args[0].keys()):
        return JSONResponse(status_code=400, content={"detail": exc.args[0]})
    # Default
    return JSONResponse(status_code=500, content={"detail": "Internal Server Error"})
