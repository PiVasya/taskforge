from __future__ import annotations

import time
from typing import Any, Dict, Optional

import requests

from config import (
    AGENT_API_BASE_URL,
    AGENT_INTERNAL_KEY,
    AGENT_REQUEST_TIMEOUT_SECONDS,
    AGENT_WORKER_ID,
)
from log import log_event, preview_text


class AgentApiClient:
    """Small client for the future TaskForge internal agent API.

    The API endpoints can be added on the ASP.NET side later. Until
    TASKFORGE_AGENT_API_BASE_URL is configured, the worker stays in safe idle mode
    and can still be smoke-tested locally through process_job().
    """

    def __init__(self, session: Optional[requests.Session] = None) -> None:
        self.session = session or requests.Session()
        self.base_url = (AGENT_API_BASE_URL or "").rstrip("/")

    @property
    def configured(self) -> bool:
        return bool(self.base_url)

    def _headers(self) -> Dict[str, str]:
        headers = {
            "Content-Type": "application/json",
            "X-TaskForge-Worker-Id": AGENT_WORKER_ID,
        }
        if AGENT_INTERNAL_KEY:
            headers["X-Internal-Key"] = AGENT_INTERNAL_KEY
        return headers

    def _post(self, path: str, body: Dict[str, Any]) -> Dict[str, Any]:
        if not self.configured:
            raise RuntimeError("TASKFORGE_AGENT_API_BASE_URL is not configured")
        url = f"{self.base_url}{path}"
        response = self.session.post(url, headers=self._headers(), json=body, timeout=AGENT_REQUEST_TIMEOUT_SECONDS)
        if response.status_code == 204:
            return {}
        if response.status_code >= 400:
            raise RuntimeError(f"{path} HTTP {response.status_code}: {(response.text or '')[:800]}")
        try:
            return response.json()
        except Exception:
            return {"raw": response.text}

    def claim_next(self) -> Optional[Dict[str, Any]]:
        if not self.configured:
            return None
        data = self._post("/api/internal/agent/claim-next", {"workerId": AGENT_WORKER_ID})
        job = data.get("job") if isinstance(data, dict) else None
        if not job and isinstance(data, dict) and data.get("id"):
            job = data
        return job if isinstance(job, dict) else None

    def heartbeat(self, run_id: str) -> None:
        if not self.configured:
            return
        self._post(f"/api/internal/agent/runs/{run_id}/heartbeat", {"workerId": AGENT_WORKER_ID})

    def complete_run(self, run_id: str, result: Dict[str, Any]) -> None:
        if not self.configured:
            log_event("agent-run-local-complete", run_id=run_id, result=preview_text(str(result), 800))
            return
        self._post(f"/api/internal/agent/runs/{run_id}/complete", {"workerId": AGENT_WORKER_ID, "result": result})

    def fail_run(self, run_id: str, error: Dict[str, Any]) -> None:
        if not self.configured:
            log_event("agent-run-local-fail", run_id=run_id, error=preview_text(str(error), 800))
            return
        self._post(f"/api/internal/agent/runs/{run_id}/fail", {"workerId": AGENT_WORKER_ID, "error": error})

    def append_step(self, run_id: str, step: Dict[str, Any]) -> None:
        if not self.configured:
            return
        self._post(f"/api/internal/agent/runs/{run_id}/steps", {"workerId": AGENT_WORKER_ID, "step": step})

    def run_tests(self, language: str, code: str, test_cases: list[dict[str, Any]], policy_forbidden_calls: Optional[list[str]] = None, policy_required_calls: Optional[list[str]] = None) -> Dict[str, Any]:
        if not self.configured:
            raise RuntimeError("TASKFORGE_AGENT_API_BASE_URL is not configured; cannot run code tests")
        return self._post("/api/internal/agent/tools/run-tests", {
            "language": language or "cpp",
            "code": code or "",
            "testCases": test_cases or [],
            "policyForbiddenCalls": policy_forbidden_calls or [],
            "policyRequiredCalls": policy_required_calls or [],
            "timeLimitMs": 3000,
            "memoryLimitMb": 256,
        })


def sleep_seconds(seconds: float) -> None:
    time.sleep(max(0.0, seconds))
