"""HTTP helpers for the TaskForge backend API."""

import json
import time
from typing import Any, Dict, List, Optional

from config import API_BASE, API_KEY, WORKER_ID, CAPABILITIES, session
from log import log, log_debug, logger


def post(
    path: str,
    payload: Dict[str, Any],
    expected: Optional[List[int]] = None,
    quiet: bool = False,
):
    expected = expected or [200]
    if not quiet:
        log_debug(f"POST >>> {path} payload_len={len(json.dumps(payload, ensure_ascii=False))}")
    started = time.time()
    resp = session.post(f"{API_BASE}{path}", json=payload, timeout=30)
    elapsed_ms = int((time.time() - started) * 1000)
    if not quiet or resp.status_code not in expected:
        log_debug(f"POST <<< {path} status={resp.status_code} {elapsed_ms}ms")
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
        return None
    job = resp.json()
    log(f"\u2192 picked job id={job.get('id')} type={job.get('type')} priority={job.get('priority')}")
    return job


def heartbeat(job_id: str):
    post(
        f"/api/internal/ai/jobs/{job_id}/heartbeat",
        {"workerId": WORKER_ID},
        expected=[200, 404],
        quiet=True,
    )


def complete(job_id: str, result: Dict[str, Any]):
    from config import OLLAMA_MODEL

    result_json = json.dumps(result, ensure_ascii=False)
    log(f"\u2713 complete job={job_id} result_len={len(result_json)}")
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
    logger.warning(f"\u2717 fail job={job_id} retryable={retryable} error={error_text[:200]}")
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
