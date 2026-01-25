import os
import subprocess
import tempfile
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field


app = FastAPI(title="taskforge image pascal runner")


class RenderRequest(BaseModel):
    source: str = Field(..., description="Pascal source code")
    timeout_seconds: int = Field(5, ge=1, le=60)


FPC = os.getenv("FPC", "fpc")


@app.get("/health")
def health():
    return {"ok": True}


@app.post("/render", response_class=FileResponse)
def render(req: RenderRequest):
    """Compiles and runs Pascal. User program must write /tmp/out.png."""
    with tempfile.TemporaryDirectory(prefix="tfr-img-pas-") as td:
        td_path = Path(td)
        src_path = td_path / "main.pas"
        bin_path = td_path / "main"
        out_png = td_path / "out.png"

        src_path.write_text(req.source, encoding="utf-8")

        # Compile
        try:
            cp = subprocess.run(
                [FPC, "-O2", "-g-", f"-o{bin_path}", str(src_path)],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=30,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "compile timeout")

        if cp.returncode != 0:
            raise HTTPException(400, f"compile failed:\n{cp.stdout[-4000:]}")

        # Run: contract - writes out.png in workdir.
        try:
            rp = subprocess.run(
                [str(bin_path)],
                cwd=td,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=req.timeout_seconds,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "run timeout")

        if rp.returncode != 0:
            raise HTTPException(400, f"runtime error:\n{rp.stdout[-4000:]}")

        if not out_png.exists() or out_png.stat().st_size == 0:
            # Give a helpful hint.
            hint = (
                "no out.png produced. Your program must write 'out.png' in current directory.\n"
                "Tip: use units fpimage, fpwritepng.\n"
                "Example (sketch):\n"
                "  uses FPImage, FPWritePNG; ... WritePNG(out.png)"
            )
            raise HTTPException(400, hint)

        return FileResponse(
            path=str(out_png),
            media_type="image/png",
            filename="out.png",
        )
