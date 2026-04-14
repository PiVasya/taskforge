"""Environment configuration, constants and shared HTTP session."""

import json
import os
import socket
from typing import Any

import requests


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() not in {"0", "false", "no", "off", ""}


def _env_csv(name: str, default: str = "") -> list[str]:
    raw = os.getenv(name, default)
    return [item.strip() for item in str(raw).split(",") if item.strip()]


def _env_json(name: str, default: Any) -> Any:
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        return json.loads(raw)
    except Exception:
        return default


# Backend API
API_BASE: str = os.getenv("TASKFORGE_API_BASE", "http://api:8080").rstrip("/")
API_KEY: str = os.getenv("TASKFORGE_INTERNAL_KEY", "")
WORKER_ID: str = os.getenv("TASKFORGE_AI_WORKER_ID", f"ai-worker-{socket.gethostname()}")

# External / local LLM provider
EXTERNAL_AI_PROVIDER: str = (os.getenv("TASKFORGE_EXTERNAL_AI_PROVIDER", "openai_compatible") or "openai_compatible").strip().lower()
EXTERNAL_AI_BASE_URL: str = os.getenv("TASKFORGE_EXTERNAL_AI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
EXTERNAL_AI_API_KEY: str = os.getenv("TASKFORGE_EXTERNAL_AI_API_KEY", "")
EXTERNAL_AI_MODEL: str = os.getenv("TASKFORGE_EXTERNAL_AI_MODEL", "qwen/qwen3.6-plus")
EXTERNAL_AI_TEMPERATURE: float = float(os.getenv("TASKFORGE_EXTERNAL_AI_TEMPERATURE", os.getenv("OLLAMA_TEMPERATURE", "0.15")))
EXTERNAL_AI_JSON_MODE: bool = _env_bool("TASKFORGE_EXTERNAL_AI_JSON_MODE", _env_bool("TASKFORGE_AI_OLLAMA_JSON_MODE", True))
EXTERNAL_AI_REQUEST_ATTEMPTS: int = int(os.getenv("TASKFORGE_EXTERNAL_AI_REQUEST_ATTEMPTS", os.getenv("TASKFORGE_AI_OLLAMA_REQUEST_ATTEMPTS", "2")))
EXTERNAL_AI_RETRY_BACKOFF_SECONDS: int = int(os.getenv("TASKFORGE_EXTERNAL_AI_RETRY_BACKOFF_SECONDS", os.getenv("TASKFORGE_AI_OLLAMA_RETRY_BACKOFF_SECONDS", "8")))
EXTERNAL_AI_EXTRA_HEADERS_RAW: str = os.getenv("TASKFORGE_EXTERNAL_AI_EXTRA_HEADERS", "")
EXTERNAL_AI_ANTHROPIC_VERSION: str = os.getenv("TASKFORGE_EXTERNAL_AI_ANTHROPIC_VERSION", "2023-06-01")
EXTERNAL_AI_TOP_P: float = float(os.getenv("TASKFORGE_EXTERNAL_AI_TOP_P", "0.85"))
EXTERNAL_AI_TOP_K: int = int(os.getenv("TASKFORGE_EXTERNAL_AI_TOP_K", "40"))

# OpenRouter / structured outputs
OPENROUTER_APP_URL: str = os.getenv("OPENROUTER_APP_URL", "").strip()
OPENROUTER_APP_TITLE: str = os.getenv("OPENROUTER_APP_TITLE", "TaskForge External AI Worker").strip()
OPENROUTER_CATEGORIES: str = os.getenv("OPENROUTER_CATEGORIES", "education,code-generation").strip()
OPENROUTER_PROVIDER_ORDER: list[str] = _env_csv("OPENROUTER_PROVIDER_ORDER")
OPENROUTER_REQUIRE_PARAMETERS: bool = _env_bool("OPENROUTER_REQUIRE_PARAMETERS", True)
OPENROUTER_ALLOW_FALLBACKS: bool = _env_bool("OPENROUTER_ALLOW_FALLBACKS", True)
OPENROUTER_DATA_COLLECTION: str = os.getenv("OPENROUTER_DATA_COLLECTION", "deny").strip()
OPENROUTER_ZDR: bool = _env_bool("OPENROUTER_ZDR", False)
OPENROUTER_PLUGINS: list[str] = _env_csv("OPENROUTER_PLUGINS", "response-healing,context-compression")
STRUCTURED_OUTPUTS: bool = _env_bool("TASKFORGE_AI_STRUCTURED_OUTPUTS", True)
ASSISTANT_PREFILL: bool = _env_bool("TASKFORGE_AI_ASSISTANT_PREFILL", True)
SCHEMA_VALIDATION_MODE: str = (os.getenv("TASKFORGE_AI_SCHEMA_VALIDATION", "strict") or "strict").strip().lower()
CHAT_STRICT_MODE: bool = _env_bool("TASKFORGE_AI_CHAT_STRICT_MODE", True)
SELECTION_TELEMETRY: bool = _env_bool("TASKFORGE_AI_SELECTION_TELEMETRY", True)

# Stage-aware provider routing
STAGE_PROVIDER_ORDER_PLANNING: list[str] = _env_csv("TASKFORGE_AI_PROVIDER_ORDER_PLANNING", "anthropic,openai")
STAGE_PROVIDER_ORDER_CHAT: list[str] = _env_csv("TASKFORGE_AI_PROVIDER_ORDER_CHAT", "openai,anthropic")
STAGE_PROVIDER_ORDER_DRAFT: list[str] = _env_csv("TASKFORGE_AI_PROVIDER_ORDER_DRAFT", "openai,anthropic")
STAGE_PROVIDER_ORDER_REPAIR: list[str] = _env_csv("TASKFORGE_AI_PROVIDER_ORDER_REPAIR", "openai,anthropic")
STAGE_PROVIDER_ORDER_REVIEW: list[str] = _env_csv("TASKFORGE_AI_PROVIDER_ORDER_REVIEW", "openai,anthropic")
STAGE_ROUTING_OVERRIDES: dict[str, Any] = _env_json("TASKFORGE_AI_STAGE_ROUTING_JSON", {}) if isinstance(_env_json("TASKFORGE_AI_STAGE_ROUTING_JSON", {}), dict) else {}

# Provider selection / legacy Ollama compatibility
_active_provider = EXTERNAL_AI_PROVIDER or "openai_compatible"
if _active_provider in {"", "auto"}:
    _active_provider = "openai_compatible" if EXTERNAL_AI_API_KEY else "ollama"
ACTIVE_PROVIDER: str = _active_provider
OLLAMA_BASE: str = os.getenv("OLLAMA_BASE_URL", "http://ollama:11434").rstrip("/")
OLLAMA_MODEL: str = os.getenv("OLLAMA_MODEL", "qwen3:14b")
ACTIVE_MODEL: str = EXTERNAL_AI_MODEL if ACTIVE_PROVIDER != "ollama" else OLLAMA_MODEL
OLLAMA_NUM_CTX: int = int(os.getenv("OLLAMA_NUM_CTX", os.getenv("OLLAMA_CONTEXT_LENGTH", "16384")))
OLLAMA_TEMPERATURE: float = float(os.getenv("OLLAMA_TEMPERATURE", str(EXTERNAL_AI_TEMPERATURE)))
OLLAMA_JSON_MODE: bool = _env_bool("TASKFORGE_AI_OLLAMA_JSON_MODE", EXTERNAL_AI_JSON_MODE)

# Worker behaviour
POLL_INTERVAL: int = int(os.getenv("POLL_INTERVAL_SECONDS", "8"))
CAPABILITIES: list[str] = [x.strip() for x in os.getenv("TASKFORGE_AI_CAPABILITIES", "*").split(",") if x.strip()]
TIMEOUT: int = int(os.getenv("TASKFORGE_AI_TIMEOUT_SECONDS", "240"))
OLLAMA_REQUEST_ATTEMPTS: int = int(os.getenv("TASKFORGE_AI_OLLAMA_REQUEST_ATTEMPTS", str(EXTERNAL_AI_REQUEST_ATTEMPTS)))
OLLAMA_RETRY_BACKOFF_SECONDS: int = int(os.getenv("TASKFORGE_AI_OLLAMA_RETRY_BACKOFF_SECONDS", str(EXTERNAL_AI_RETRY_BACKOFF_SECONDS)))
MAX_JOB_RETRIES: int = int(os.getenv("TASKFORGE_AI_MAX_JOB_RETRIES", "3"))
RETRYABLE_STAGE_DELAY_SECONDS: int = int(os.getenv("TASKFORGE_AI_RETRYABLE_STAGE_DELAY_SECONDS", "45"))
PLANNER_FALLBACK_AFTER_RETRY_COUNT: int = int(os.getenv("TASKFORGE_AI_PLANNER_FALLBACK_AFTER_RETRY_COUNT", "2"))
DRAFT_SUBSTAGES: bool = _env_bool("TASKFORGE_AI_DRAFT_SUBSTAGES", False)
ROUTE_AWARE_REPAIR: bool = _env_bool("TASKFORGE_AI_ROUTE_AWARE_REPAIR", True)
SANDBOX_RUNNER: bool = _env_bool("TASKFORGE_AI_SANDBOX_RUNNER", False)
SANDBOX_RUNNER_MODE: str = (os.getenv("TASKFORGE_AI_SANDBOX_MODE", "process") or "process").strip().lower()
RUNNER_TIMEOUT_SECONDS: int = int(os.getenv("TASKFORGE_AI_RUNNER_TIMEOUT_SECONDS", "6"))
RUNNER_CPU_SECONDS: int = int(os.getenv("TASKFORGE_AI_RUNNER_CPU_SECONDS", "4"))
RUNNER_MEMORY_MB: int = int(os.getenv("TASKFORGE_AI_RUNNER_MEMORY_MB", "256"))
RUNNER_STDOUT_LIMIT: int = int(os.getenv("TASKFORGE_AI_RUNNER_STDOUT_LIMIT", "32768"))
RUNNER_STDERR_LIMIT: int = int(os.getenv("TASKFORGE_AI_RUNNER_STDERR_LIMIT", "32768"))
RUNNER_CONTAINER_RUNTIME: str = (os.getenv("TASKFORGE_AI_RUNNER_RUNTIME", "docker") or "docker").strip()
RUNNER_CONTAINER_IMAGE: str = (os.getenv("TASKFORGE_AI_RUNNER_CONTAINER_IMAGE", "taskforge-python-runner:wave4") or "taskforge-python-runner:wave4").strip()
RUNNER_CONTAINER_NETWORK: str = (os.getenv("TASKFORGE_AI_RUNNER_NETWORK", "none") or "none").strip()
RUNNER_CONTAINER_TMPFS_MB: int = int(os.getenv("TASKFORGE_AI_RUNNER_TMPFS_MB", "16"))
RUNNER_CONTAINER_PIDS_LIMIT: int = int(os.getenv("TASKFORGE_AI_RUNNER_PIDS_LIMIT", "64"))
RUNNER_CONTAINER_NO_NEW_PRIVS: bool = _env_bool("TASKFORGE_AI_RUNNER_NO_NEW_PRIVS", True)

# Stage budgets
COURSE_PROFILE_TIMEOUT: int = int(os.getenv("TASKFORGE_AI_COURSE_PROFILE_TIMEOUT_SECONDS", "90"))
GAP_ANALYSIS_TIMEOUT: int = int(os.getenv("TASKFORGE_AI_GAP_ANALYSIS_TIMEOUT_SECONDS", "70"))
BATCH_PLAN_TIMEOUT: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_TIMEOUT_SECONDS", "70"))
BRIEF_TIMEOUT: int = int(os.getenv("TASKFORGE_AI_BRIEF_TIMEOUT_SECONDS", "90"))
REFERENCE_PACK_TIMEOUT: int = int(os.getenv("TASKFORGE_AI_REFERENCE_PACK_TIMEOUT_SECONDS", "90"))
GENERATION_TIMEOUT: int = int(os.getenv("TASKFORGE_AI_GENERATION_TIMEOUT_SECONDS", str(TIMEOUT)))

COURSE_PROFILE_NUM_PREDICT: int = int(os.getenv("TASKFORGE_AI_COURSE_PROFILE_NUM_PREDICT", "520"))
GAP_ANALYSIS_NUM_PREDICT: int = int(os.getenv("TASKFORGE_AI_GAP_ANALYSIS_NUM_PREDICT", "420"))
BATCH_PLAN_NUM_PREDICT: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_NUM_PREDICT", "420"))
BRIEF_NUM_PREDICT: int = int(os.getenv("TASKFORGE_AI_BRIEF_NUM_PREDICT", "620"))
REFERENCE_PACK_NUM_PREDICT: int = int(os.getenv("TASKFORGE_AI_REFERENCE_PACK_NUM_PREDICT", "620"))
GENERATION_NUM_PREDICT: int = int(os.getenv("TASKFORGE_AI_GENERATION_NUM_PREDICT", "1800"))

COURSE_PROFILE_MAX_RETRIES: int = int(os.getenv("TASKFORGE_AI_COURSE_PROFILE_MAX_RETRIES", "2"))
GAP_ANALYSIS_MAX_RETRIES: int = int(os.getenv("TASKFORGE_AI_GAP_ANALYSIS_MAX_RETRIES", "2"))
BATCH_PLAN_MAX_RETRIES: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_MAX_RETRIES", "3"))
REFERENCE_PACK_MAX_RETRIES: int = int(os.getenv("TASKFORGE_AI_REFERENCE_PACK_MAX_RETRIES", "2"))
BRIEF_MAX_RETRIES: int = int(os.getenv("TASKFORGE_AI_BRIEF_MAX_RETRIES", "2"))

DEEPSEEK_RECOMMENDED_MODEL: str = os.getenv("TASKFORGE_AI_DEEPSEEK_MODEL", "deepseek-r1:14b")

# Quality gates defaults
MAX_REFERENCE_ASSIGNMENTS: int = int(os.getenv("TASKFORGE_AI_MAX_REFERENCE_ASSIGNMENTS", "20"))
MIN_PUBLIC_TESTS: int = int(os.getenv("TASKFORGE_AI_MIN_PUBLIC_TESTS", "1"))
MIN_HIDDEN_TESTS: int = int(os.getenv("TASKFORGE_AI_MIN_HIDDEN_TESTS", "0"))
MAX_HIDDEN_TESTS: int = int(os.getenv("TASKFORGE_AI_MAX_HIDDEN_TESTS", "8"))
MIN_TOTAL_TESTS: int = int(os.getenv("TASKFORGE_AI_MIN_TOTAL_TESTS", "1"))
MIN_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_MIN_DESCRIPTION_LEN", "200"))
MAX_REPAIR_ATTEMPTS: int = int(os.getenv("TASKFORGE_AI_REPAIR_ATTEMPTS", "1"))
MAX_REFERENCE_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_REFERENCE_DESCRIPTION_LEN", "260"))
GAP_ANALYSIS_REFERENCE_ASSIGNMENTS: int = int(os.getenv("TASKFORGE_AI_GAP_REFERENCE_ASSIGNMENTS", "8"))
BATCH_PLAN_REFERENCE_ASSIGNMENTS: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_REFERENCE_ASSIGNMENTS", "6"))
BATCH_PLAN_REFERENCE_ASSIGNMENTS_RETRY: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_REFERENCE_ASSIGNMENTS_RETRY", "3"))
BATCH_PLAN_REFERENCE_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_REFERENCE_DESCRIPTION_LEN", "140"))
GAP_ANALYSIS_REFERENCE_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_GAP_REFERENCE_DESCRIPTION_LEN", "160"))

# Stronger duplicate detection
DUPLICATE_SIGNATURES: bool = _env_bool("TASKFORGE_AI_DUPLICATE_SIGNATURES", True)
SIMILARITY_SIGNATURE_WARNING_THRESHOLD: float = float(os.getenv("TASKFORGE_AI_SIMILARITY_SIGNATURE_WARNING", "0.66"))
SIMILARITY_SIGNATURE_FAIL_THRESHOLD: float = float(os.getenv("TASKFORGE_AI_SIMILARITY_SIGNATURE_FAIL", "0.82"))
SIMILARITY_SIGNATURE_HINTS_LIMIT: int = int(os.getenv("TASKFORGE_AI_SIMILARITY_SIGNATURE_HINTS_LIMIT", "5"))
DUPLICATE_CLUSTERING: bool = _env_bool("TASKFORGE_AI_DUPLICATE_CLUSTERING", True)
DUPLICATE_CLUSTER_THRESHOLD: float = float(os.getenv("TASKFORGE_AI_DUPLICATE_CLUSTER_THRESHOLD", "0.72"))
DUPLICATE_CLUSTER_LIMIT: int = int(os.getenv("TASKFORGE_AI_DUPLICATE_CLUSTER_LIMIT", "8"))
DUPLICATE_CLUSTER_WARNING_SIZE: int = int(os.getenv("TASKFORGE_AI_DUPLICATE_CLUSTER_WARNING_SIZE", "2"))
DUPLICATE_CLUSTER_FAIL_SIZE: int = int(os.getenv("TASKFORGE_AI_DUPLICATE_CLUSTER_FAIL_SIZE", "3"))
DUPLICATE_CLUSTER_PREVIEW_GROUPS: int = int(os.getenv("TASKFORGE_AI_DUPLICATE_CLUSTER_PREVIEW_GROUPS", "3"))
WORKER_TELEMETRY_ENABLED: bool = _env_bool("TASKFORGE_AI_WORKER_TELEMETRY", True)
OPENROUTER_USAGE_TELEMETRY: bool = _env_bool("TASKFORGE_AI_OPENROUTER_USAGE_TELEMETRY", True)


def parse_extra_headers(raw: str) -> dict[str, str]:
    raw = (raw or "").strip()
    if not raw:
        return {}
    try:
        parsed = json.loads(raw)
        if isinstance(parsed, dict):
            return {str(k): str(v) for k, v in parsed.items() if str(k).strip()}
    except Exception:
        pass
    headers: dict[str, str] = {}
    for line in raw.splitlines():
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        key = key.strip()
        value = value.strip()
        if key:
            headers[key] = value
    return headers


EXTERNAL_AI_EXTRA_HEADERS = parse_extra_headers(EXTERNAL_AI_EXTRA_HEADERS_RAW)

# Shared HTTP session
session: requests.Session = requests.Session()
session.headers.update({"X-Internal-Key": API_KEY})
