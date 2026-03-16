import base64
import os
import shutil
import signal
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field
from PIL import Image

app = FastAPI(title="taskforge image cpp runner")

class RenderRequest(BaseModel):
    source: str = Field(min_length=1)
    stdin: str | None = None
    timeoutSeconds: int = Field(default=20, ge=1, le=120)
    timeout_seconds: int | None = Field(default=None, ge=1, le=120)

class RenderDebugResponse(BaseModel):
    pngBase64: str | None = None
    stdout: str = ""
    stderr: str = ""


def _tail(s: str, n: int = 8000) -> str:
    return s if len(s) <= n else ('…' + s[-n:])


def _timeout(req: RenderRequest) -> int:
    return int(req.timeout_seconds or req.timeoutSeconds or 20)


def _compile(src: Path, exe: Path, timeout: int) -> tuple[int, str]:
    cmd = ["g++", str(src), "-O2", "-std=c++17", "-lglut", "-lGL", "-lGLU", "-o", str(exe)]
    cp = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=timeout)
    return cp.returncode, cp.stdout or ""


def _find_output_file(tdir: Path) -> Path | None:
    names = ["out.png", "out.ppm", "out.bmp", "out.jpg", "out.jpeg"]
    for n in names:
        p = tdir / n
        if p.exists() and p.stat().st_size > 0:
            return p
    images = sorted([p for p in tdir.iterdir() if p.is_file() and p.suffix.lower() in ('.png','.ppm','.bmp','.jpg','.jpeg')], key=lambda p: p.stat().st_mtime, reverse=True)
    return images[0] if images else None


def _convert_to_png(src: Path, out_png: Path) -> bytes:
    if src.suffix.lower() == '.png':
        return src.read_bytes()
    img = Image.open(src)
    img.save(out_png, format='PNG')
    return out_png.read_bytes()


def _window_capture(exe: Path, tdir: Path, stdin: str | None, timeout: int) -> tuple[bytes | None, str, str, str | None]:
    display = ':99'
    xvfb = subprocess.Popen(['Xvfb', display, '-screen', '0', '1280x1024x24'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    openbox = None
    proc = None
    stdout = ''
    stderr = ''
    try:
        time.sleep(0.5)
        env = {**os.environ, 'DISPLAY': display}
        openbox = subprocess.Popen(['openbox'], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(0.5)
        proc = subprocess.Popen([str(exe)], cwd=str(tdir), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if stdin:
            try:
                proc.stdin.write(stdin)
                if not stdin.endswith('\n'):
                    proc.stdin.write('\n')
                proc.stdin.flush()
                proc.stdin.close()
            except Exception:
                pass

        output_file = None
        start = time.time()
        window_id = None
        screenshot = tdir / 'captured.png'
        while time.time() - start < timeout:
            output_file = _find_output_file(tdir)
            if output_file is not None:
                break
            if proc.poll() is not None and (time.time() - start) > 1.0:
                # one final chance after process exit
                output_file = _find_output_file(tdir)
                if output_file is not None:
                    break
            if window_id is None:
                try:
                    res = subprocess.run(['xdotool', 'search', '--sync', '--onlyvisible', '--pid', str(proc.pid)], env=env, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, timeout=1)
                    ids = [x.strip() for x in (res.stdout or '').splitlines() if x.strip()]
                    if ids:
                        window_id = ids[-1]
                        time.sleep(0.8)
                except Exception:
                    pass
            if window_id:
                try:
                    subprocess.run(['import', '-display', display, '-window', window_id, str(screenshot)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=3)
                    if screenshot.exists() and screenshot.stat().st_size > 0:
                        return screenshot.read_bytes(), '', '', None
                except Exception:
                    pass
            time.sleep(0.25)

        try:
            out, err = proc.communicate(timeout=2)
            stdout += out or ''
            stderr += err or ''
        except Exception:
            pass

        if output_file is not None:
            out_png = tdir / 'out.png'
            return _convert_to_png(output_file, out_png), stdout, stderr, None

        if window_id and screenshot.exists() and screenshot.stat().st_size > 0:
            return screenshot.read_bytes(), stdout, stderr, None

        if proc and proc.returncode not in (None, 0):
            return None, stdout, stderr, f'Program exited with code {proc.returncode} before a visible frame was captured.'
        return None, stdout, stderr, 'No image file was produced and no visible window could be captured.'
    finally:
        for child in (proc, openbox, xvfb):
            if child is None:
                continue
            try:
                child.terminate()
            except Exception:
                pass
        time.sleep(0.2)
        for child in (proc, openbox, xvfb):
            if child is None:
                continue
            try:
                child.kill()
            except Exception:
                pass


@app.get('/health')
def health():
    return {'ok': True}


@app.post('/render')
def render(req: RenderRequest):
    timeout = _timeout(req)
    with tempfile.TemporaryDirectory(prefix='tf-img-cpp-') as td:
        tdir = Path(td)
        src = tdir / 'main.cpp'
        exe = tdir / 'main'
        src.write_text(req.source, encoding='utf-8')
        try:
            rc, compile_out = _compile(src, exe, min(timeout, 30))
        except subprocess.TimeoutExpired:
            raise HTTPException(408, {'message': 'Compilation timed out', 'stdout': '', 'stderr': ''})
        if rc != 0:
            raise HTTPException(400, {'message': 'Compilation failed', 'stdout': '', 'stderr': _tail(compile_out)})
        png, stdout, stderr, err = _window_capture(exe, tdir, req.stdin, timeout)
        if not png:
            raise HTTPException(400, {'message': err or 'Rendering failed', 'stdout': _tail(stdout), 'stderr': _tail(stderr)})
        return Response(content=png, media_type='image/png')


@app.post('/render/debug', response_model=RenderDebugResponse)
def render_debug(req: RenderRequest):
    timeout = _timeout(req)
    with tempfile.TemporaryDirectory(prefix='tf-img-cpp-') as td:
        tdir = Path(td)
        src = tdir / 'main.cpp'
        exe = tdir / 'main'
        src.write_text(req.source, encoding='utf-8')
        try:
            rc, compile_out = _compile(src, exe, min(timeout, 30))
        except subprocess.TimeoutExpired:
            raise HTTPException(408, {'message': 'Compilation timed out', 'stdout': '', 'stderr': ''})
        if rc != 0:
            raise HTTPException(400, {'message': 'Compilation failed', 'stdout': '', 'stderr': _tail(compile_out)})
        png, stdout, stderr, err = _window_capture(exe, tdir, req.stdin, timeout)
        if not png:
            raise HTTPException(400, {'message': err or 'Rendering failed', 'stdout': _tail(stdout), 'stderr': _tail(stderr)})
        return RenderDebugResponse(pngBase64=base64.b64encode(png).decode('ascii'), stdout=stdout, stderr=stderr)
