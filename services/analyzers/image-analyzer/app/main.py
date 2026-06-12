import logging
import os
import time

from fastapi import FastAPI, File, UploadFile, Request
from fastapi.responses import JSONResponse

from .similarity import ImageComparator


app = FastAPI(title="taskforge-image-analyzer", version="1.0.0")
logger = logging.getLogger("taskforge.image_analyzer")


def taskforge_debug_logs_enabled() -> bool:
    value = os.getenv("TASKFORGE_DEBUG_LOGS", "0").strip().lower()
    return value in {"1", "true", "yes", "on", "debug"}


@app.middleware("http")
async def taskforge_debug_request_log(request: Request, call_next):
    if not taskforge_debug_logs_enabled():
        return await call_next(request)

    started = time.perf_counter()
    trace_id = request.headers.get("x-taskforge-trace-id") or f"image-analyzer-{time.time_ns()}"
    caller = request.headers.get("x-taskforge-caller-service") or request.headers.get("x-forwarded-host") or "unknown"
    logger.info(
        "[TFDBG IMAGE IN START] trace=%s service=image-analyzer caller=%s method=%s path=%s query=%s content_type=%s content_length=%s",
        trace_id,
        caller,
        request.method,
        request.url.path,
        request.url.query,
        request.headers.get("content-type"),
        request.headers.get("content-length"),
    )
    try:
        response = await call_next(request)
    except Exception:
        elapsed_ms = (time.perf_counter() - started) * 1000
        logger.exception(
            "[TFDBG IMAGE IN EXCEPTION] trace=%s service=image-analyzer method=%s path=%s duration_ms=%.2f",
            trace_id,
            request.method,
            request.url.path,
            elapsed_ms,
        )
        raise
    elapsed_ms = (time.perf_counter() - started) * 1000
    response.headers["X-TaskForge-Trace-Id"] = trace_id
    logger.info(
        "[TFDBG IMAGE IN END] trace=%s service=image-analyzer caller=%s method=%s path=%s status=%s duration_ms=%.2f response_type=%s",
        trace_id,
        caller,
        request.method,
        request.url.path,
        response.status_code,
        elapsed_ms,
        response.headers.get("content-type"),
    )
    return response


_comparator: ImageComparator | None = None


@app.on_event("startup")
def _startup() -> None:
    logger.info("[taskforge-debug] logs=%s service=image-analyzer", "on" if taskforge_debug_logs_enabled() else "off")
    # Load model once on startup.
    global _comparator
    _comparator = ImageComparator()
    if taskforge_debug_logs_enabled():
        logger.info("[taskforge-debug] model loaded device=%s", _comparator.device_name)


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

    if taskforge_debug_logs_enabled():
        logger.info(
            "[TFDBG IMAGE COMPARE INPUT] service=image-analyzer expected_file=%s expected_bytes=%d actual_file=%s actual_bytes=%d threshold=%.4f",
            expected.filename,
            len(exp_b),
            actual.filename,
            len(act_b),
            threshold,
        )

    if _comparator is None:
        # Should not happen, but keep it safe.
        if taskforge_debug_logs_enabled():
            logger.error("[TFDBG IMAGE COMPARE ERROR] service=image-analyzer reason=model_not_loaded")
        return JSONResponse(status_code=503, content={"error": "Model not loaded"})

    r = _comparator.compare(exp_b, act_b)

    passed = r.clip_similarity >= threshold
    if taskforge_debug_logs_enabled():
        logger.info(
            "[TFDBG IMAGE COMPARE RESULT] service=image-analyzer passed=%s threshold=%.4f clip=%.6f phash=%.6f combined=%.6f model=%s device=%s",
            passed,
            threshold,
            r.clip_similarity,
            r.phash_similarity,
            r.combined_similarity,
            r.model,
            _comparator.device_name,
        )

    return {
        "passed": passed,
        "threshold": threshold,
        "clip_similarity": round(r.clip_similarity, 6),
        "phash_similarity": round(r.phash_similarity, 6),
        "combined_similarity": round(r.combined_similarity, 6),
        "model": r.model,
        "device": _comparator.device_name,
    }
