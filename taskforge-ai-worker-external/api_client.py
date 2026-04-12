"""HTTP helpers for the TaskForge backend API."""

import json
import time
from typing import Any, Dict, List, Optional

from config import API_BASE, WORKER_ID, CAPABILITIES, session
from log import log, log_debug, logger, log_event, preview_text


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
    log_event('job-picked', job=_job_summary(job))
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
    from config import ACTIVE_MODEL

    telemetry = result.pop("__workerTelemetry", None) if isinstance(result, dict) else None
    telemetry_json = json.dumps(telemetry, ensure_ascii=False) if isinstance(telemetry, dict) else None
    result_json = json.dumps(result, ensure_ascii=False)
    status = result.get("status") if isinstance(result, dict) else None
    score = result.get("score") if isinstance(result, dict) else None
    keys = sorted(result.keys())[:12] if isinstance(result, dict) else []
    log_event('job-complete-api', job_id=job_id, model=ACTIVE_MODEL, result_len=len(result_json), telemetry_len=(len(telemetry_json) if telemetry_json else 0), status=status, score=score, keys=keys, result_preview=preview_text(result_json, 500))
    resp = post(
        f"/api/internal/ai/jobs/{job_id}/complete",
        {
            "workerId": WORKER_ID,
            "modelName": ACTIVE_MODEL,
            "resultJson": result_json,
            "telemetryJson": telemetry_json,
        },
        expected=[200, 404, 409],
    )
    if resp.status_code == 409:
        body = (resp.text or "")[:2000]
        if "SAVE_CONFLICT" in body or "AiGeneratedAssignmentDrafts" in body or "BatchItemId" in body or "JobId" in body:
            log_event('job-complete-conflict-ignored', level='warning', job_id=job_id, status=resp.status_code, body=_preview(body, 240))
            return
        raise RuntimeError(f"POST /api/internal/ai/jobs/{job_id}/complete -> 409: {body[:500]}")


def fail(job_id: str, error_text: str, retryable: bool = True, retry_delay_seconds: int = 120):
    log_event('job-fail-api', level='warning', job_id=job_id, retryable=retryable, retry_delay_seconds=retry_delay_seconds, error=error_text[:500])
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
