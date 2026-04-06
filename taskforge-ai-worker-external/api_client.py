"""HTTP helpers for the TaskForge backend API."""

import json
import time
from typing import Any, Dict, List, Optional

from config import API_BASE, WORKER_ID, CAPABILITIES, session
from log import log, log_debug, logger


def _preview(value: Any, limit: int = 160) -> str:
    if value is None:
        return "-"
    text = str(value).replace("\n", " ").replace("\r", " ").strip()
    if len(text) <= limit:
        return text
    return text[: limit - 3] + "..."


def _job_summary(job: Dict[str, Any]) -> str:
    parts = [
        f"job={job.get('id')}",
        f"type={job.get('type')}",
        f"priority={job.get('priority')}",
    ]
    if job.get("stageCode") or job.get("stageLabel"):
        parts.append(f"stage={job.get('stageCode') or '-'}")
        parts.append(f"stageLabel={_preview(job.get('stageLabel'), 80)}")
    if job.get("targetEntityType") or job.get("targetEntityId"):
        parts.append(f"target={job.get('targetEntityType') or '-'}:{job.get('targetEntityId') or '-'}")
    if job.get("courseId"):
        parts.append(f"course={job.get('courseId')}")
    return " ".join(parts)


def post(
    path: str,
    payload: Dict[str, Any],
    expected: Optional[List[int]] = None,
    quiet: bool = False,
):
    expected = expected or [200]
    payload_text = json.dumps(payload, ensure_ascii=False)
    if not quiet:
        log_debug(f"POST >>> path={path} payload_len={len(payload_text)} payload={_preview(payload_text, 220)}")
    started = time.time()
    resp = session.post(f"{API_BASE}{path}", json=payload, timeout=30)
    elapsed_ms = int((time.time() - started) * 1000)
    if not quiet or resp.status_code not in expected:
        log_debug(f"POST <<< path={path} status={resp.status_code} elapsed_ms={elapsed_ms} body={_preview(resp.text, 220)}")
    if resp.status_code not in expected:
        raise RuntimeError(f"POST {path} -> {resp.status_code}: {resp.text[:500]}")
    return resp


def pull_job() -> Optional[Dict[str, Any]]:
    resp = post(
        "/api/internal/ai/jobs/pull",
        {"workerId": WORKER_ID, "capabilities": CAPABILITIES},
        expected=[200, 204],
        quiet=True,
    )
    if resp.status_code == 204:
        log_debug(f"pull idle worker={WORKER_ID} caps={CAPABILITIES}")
        return None
    job = resp.json()
    log(f"→ picked {_job_summary(job)}")
    return job


def heartbeat(job_id: str):
    log_debug(f"heartbeat >>> job={job_id} worker={WORKER_ID}")
    post(
        f"/api/internal/ai/jobs/{job_id}/heartbeat",
        {"workerId": WORKER_ID},
        expected=[200, 404],
        quiet=True,
    )
    log_debug(f"heartbeat <<< job={job_id} worker={WORKER_ID}")


def complete(job_id: str, result: Dict[str, Any]):
    from config import OLLAMA_MODEL

    result_json = json.dumps(result, ensure_ascii=False)
    status = result.get("status") if isinstance(result, dict) else None
    score = result.get("score") if isinstance(result, dict) else None
    keys = sorted(result.keys())[:12] if isinstance(result, dict) else []
    log(f"✓ complete job={job_id} model={OLLAMA_MODEL} result_len={len(result_json)} status={status} score={score} keys={keys}")
    post(
        f"/api/internal/ai/jobs/{job_id}/complete",
        {
            "workerId": WORKER_ID,
            "modelName": OLLAMA_MODEL,
            "resultJson": result_json,
        },
        expected=[200, 404],
    )


def fail(job_id: str, error_text: str, retryable: bool = True, retry_delay_seconds: int = 120):
    logger.warning(f"✗ fail job={job_id} retryable={retryable} delay={retry_delay_seconds}s error={error_text[:200]}")
    post(
        f"/api/internal/ai/jobs/{job_id}/fail",
        {
            "workerId": WORKER_ID,
            "errorText": error_text[:4000],
            "retryable": retryable,
            "retryDelaySeconds": retry_delay_seconds,
        },
        expected=[200, 404],
    )
