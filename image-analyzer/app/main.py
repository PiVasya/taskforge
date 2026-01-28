from fastapi import FastAPI, File, UploadFile
from fastapi.responses import JSONResponse

from .similarity import ImageComparator


app = FastAPI(title="taskforge-image-analyzer", version="1.0.0")


_comparator: ImageComparator | None = None


@app.on_event("startup")
def _startup() -> None:
    # Load model once on startup.
    global _comparator
    _comparator = ImageComparator()


@app.get("/health")
def health() -> dict:
    return {"ok": True}


@app.post("/compare")
async def compare(
    expected: UploadFile = File(...),
    actual: UploadFile = File(...),
    threshold: float = 0.90,
):
    if threshold < 0:
        threshold = 0.0
    if threshold > 1:
        threshold = 1.0

    exp_b = await expected.read()
    act_b = await actual.read()

    if _comparator is None:
        # Should not happen, but keep it safe.
        return JSONResponse(status_code=503, content={"error": "Model not loaded"})

    r = _comparator.compare(exp_b, act_b)

    passed = r.clip_similarity >= threshold

    return {
        "passed": passed,
        "threshold": threshold,
        "clip_similarity": round(r.clip_similarity, 6),
        "phash_similarity": round(r.phash_similarity, 6),
        "combined_similarity": round(r.combined_similarity, 6),
        "model": r.model,
        "device": _comparator.device_name,
    }
