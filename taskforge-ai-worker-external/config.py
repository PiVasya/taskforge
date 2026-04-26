from __future__ import annotations

import json
import os
from typing import Dict


def env_bool(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None or raw == "":
        return default
    return raw.strip().lower() in {"1", "true", "yes", "y", "on"}


def parse_extra_headers(raw: str | None) -> Dict[str, str]:
    value = (raw or "").strip()
    if not value:
        return {}
    try:
        parsed = json.loads(value)
        if isinstance(parsed, dict):
            return {str(k): str(v) for k, v in parsed.items() if str(k).strip()}
    except Exception:
        pass
    headers: Dict[str, str] = {}
    for line in value.splitlines():
        if ":" not in line:
            continue
        key, val = line.split(":", 1)
        key = key.strip()
        if key:
            headers[key] = val.strip()
    return headers


EXTERNAL_AI_PROVIDER = (os.getenv("TASKFORGE_EXTERNAL_AI_PROVIDER", "openai_compatible") or "openai_compatible").strip().lower()
EXTERNAL_AI_BASE_URL = os.getenv("TASKFORGE_EXTERNAL_AI_BASE_URL", "https://openrouter.ai/api/v1").rstrip("/")
EXTERNAL_AI_API_KEY = os.getenv("TASKFORGE_EXTERNAL_AI_API_KEY", "")
EXTERNAL_AI_MODEL = os.getenv("TASKFORGE_EXTERNAL_AI_MODEL", "qwen/qwen3.6-plus")
EXTERNAL_AI_TEMPERATURE = float(os.getenv("TASKFORGE_EXTERNAL_AI_TEMPERATURE", "0.15"))
EXTERNAL_AI_TOP_P = float(os.getenv("TASKFORGE_EXTERNAL_AI_TOP_P", "0.85"))
EXTERNAL_AI_TIMEOUT_SECONDS = int(os.getenv("TASKFORGE_EXTERNAL_AI_TIMEOUT_SECONDS", "180"))
EXTERNAL_AI_REQUEST_ATTEMPTS = int(os.getenv("TASKFORGE_EXTERNAL_AI_REQUEST_ATTEMPTS", "2"))
EXTERNAL_AI_RETRY_BACKOFF_SECONDS = int(os.getenv("TASKFORGE_EXTERNAL_AI_RETRY_BACKOFF_SECONDS", "8"))
EXTERNAL_AI_EXTRA_HEADERS = parse_extra_headers(os.getenv("TASKFORGE_EXTERNAL_AI_EXTRA_HEADERS"))

OPENROUTER_APP_URL = os.getenv("OPENROUTER_APP_URL", "")
OPENROUTER_APP_TITLE = os.getenv("OPENROUTER_APP_TITLE", "TaskForge")
OPENROUTER_CATEGORIES = os.getenv("OPENROUTER_CATEGORIES", "education,code-generation")
OPENROUTER_DATA_COLLECTION = os.getenv("OPENROUTER_DATA_COLLECTION", "deny")
OPENROUTER_ZDR = env_bool("OPENROUTER_ZDR", False)
OPENROUTER_REQUIRE_PARAMETERS = env_bool("OPENROUTER_REQUIRE_PARAMETERS", True)

LOG_DIR = os.getenv("TASKFORGE_AI_LOG_DIR", "/app/logs")
LOG_FILE = os.getenv("TASKFORGE_AI_LOG_FILE", f"{LOG_DIR}/openrouter-base.log")
INCLUDE_PROMPTS = env_bool("TASKFORGE_AI_LOG_INCLUDE_PROMPTS", False)
INCLUDE_RESPONSES = env_bool("TASKFORGE_AI_LOG_INCLUDE_RESPONSES", False)

# Agent worker/runtime settings. The backend endpoints are intentionally optional:
# without TASKFORGE_AGENT_API_BASE_URL the worker stays in safe idle mode and
# can still be tested locally by importing worker.process_job().
AGENT_API_BASE_URL = os.getenv("TASKFORGE_AGENT_API_BASE_URL", "").rstrip("/")
AGENT_INTERNAL_KEY = os.getenv("TASKFORGE_AGENT_INTERNAL_KEY", "")
AGENT_WORKER_ID = os.getenv("TASKFORGE_AGENT_WORKER_ID", os.getenv("HOSTNAME", "taskforge-ai-worker-external"))
AGENT_POLL_SECONDS = float(os.getenv("TASKFORGE_AGENT_POLL_SECONDS", "5"))
AGENT_HEARTBEAT_SECONDS = float(os.getenv("TASKFORGE_AGENT_HEARTBEAT_SECONDS", "15"))
AGENT_REQUEST_TIMEOUT_SECONDS = int(os.getenv("TASKFORGE_AGENT_REQUEST_TIMEOUT_SECONDS", "30"))
AGENT_IDLE_LOG_SECONDS = float(os.getenv("TASKFORGE_AGENT_IDLE_LOG_SECONDS", "60"))
AGENT_MAX_JOB_SECONDS = float(os.getenv("TASKFORGE_AGENT_MAX_JOB_SECONDS", "900"))
