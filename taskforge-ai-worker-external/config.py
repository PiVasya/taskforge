"""Environment configuration, constants and shared HTTP session for the external AI worker."""

import os
import socket

import requests


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() not in {"0", "false", "no", "off", ""}


# ── Backend API ──────────────────────────────────────
API_BASE: str = os.getenv("TASKFORGE_API_BASE", "http://api:8080").rstrip("/")
API_KEY: str = os.getenv("TASKFORGE_INTERNAL_KEY", "")
WORKER_ID: str = os.getenv("TASKFORGE_AI_WORKER_ID", f"ai-worker-external-{socket.gethostname()}")

# ── External provider ────────────────────────────────
EXTERNAL_AI_PROVIDER: str = os.getenv("TASKFORGE_EXTERNAL_AI_PROVIDER", "openai_compatible").strip().lower()
EXTERNAL_AI_BASE_URL: str = os.getenv("TASKFORGE_EXTERNAL_AI_BASE_URL", "https://api.openai.com/v1").rstrip("/")
EXTERNAL_AI_API_KEY: str = os.getenv("TASKFORGE_EXTERNAL_AI_API_KEY", "")
EXTERNAL_AI_MODEL: str = os.getenv("TASKFORGE_EXTERNAL_AI_MODEL", "gpt-4.1-mini")
EXTERNAL_AI_ANTHROPIC_VERSION: str = os.getenv("TASKFORGE_EXTERNAL_AI_ANTHROPIC_VERSION", "2023-06-01")
EXTERNAL_AI_EXTRA_HEADERS_RAW: str = os.getenv("TASKFORGE_EXTERNAL_AI_EXTRA_HEADERS", "")

EXTERNAL_AI_SYSTEM_PROMPT: str = os.getenv("TASKFORGE_EXTERNAL_AI_SYSTEM_PROMPT", "").strip()
EXTERNAL_AI_SEED_RAW: str = os.getenv("TASKFORGE_EXTERNAL_AI_SEED", "").strip()
EXTERNAL_AI_TOP_P_RAW: str = os.getenv("TASKFORGE_EXTERNAL_AI_TOP_P", "").strip()
EXTERNAL_AI_TOP_K_RAW: str = os.getenv("TASKFORGE_EXTERNAL_AI_TOP_K", "").strip()

# Qwen / DashScope specific knobs. Qwen3.6 Plus enables thinking by default,
# which is great for chat, but often hurts latency and JSON discipline in this worker.
EXTERNAL_AI_QWEN_DISABLE_THINKING: bool = _env_bool("TASKFORGE_EXTERNAL_AI_QWEN_DISABLE_THINKING", True)
EXTERNAL_AI_QWEN_ENABLE_SEARCH: bool = _env_bool("TASKFORGE_EXTERNAL_AI_QWEN_ENABLE_SEARCH", False)
EXTERNAL_AI_QWEN_THINKING_BUDGET_RAW: str = os.getenv("TASKFORGE_EXTERNAL_AI_QWEN_THINKING_BUDGET", "").strip()

# Backward-compatible aliases so the copied pipeline can stay unchanged.
OLLAMA_BASE: str = EXTERNAL_AI_BASE_URL
OLLAMA_MODEL: str = EXTERNAL_AI_MODEL
OLLAMA_NUM_CTX: int = int(os.getenv("TASKFORGE_EXTERNAL_AI_NUM_CTX", os.getenv("OLLAMA_NUM_CTX", os.getenv("OLLAMA_CONTEXT_LENGTH", "16384"))))
OLLAMA_TEMPERATURE: float = float(os.getenv("TASKFORGE_EXTERNAL_AI_TEMPERATURE", os.getenv("OLLAMA_TEMPERATURE", "0.12")))
OLLAMA_JSON_MODE: bool = _env_bool("TASKFORGE_EXTERNAL_AI_JSON_MODE", _env_bool("TASKFORGE_AI_OLLAMA_JSON_MODE", True))

# ── Worker behaviour ─────────────────────────────────
POLL_INTERVAL: int = int(os.getenv("POLL_INTERVAL_SECONDS", "8"))
CAPABILITIES: list = [
    x.strip()
    for x in os.getenv("TASKFORGE_AI_CAPABILITIES", "*").split(",")
    if x.strip()
]
TIMEOUT: int = int(os.getenv("TASKFORGE_AI_TIMEOUT_SECONDS", "240"))
OLLAMA_REQUEST_ATTEMPTS: int = int(os.getenv("TASKFORGE_EXTERNAL_AI_REQUEST_ATTEMPTS", os.getenv("TASKFORGE_AI_OLLAMA_REQUEST_ATTEMPTS", "2")))
OLLAMA_RETRY_BACKOFF_SECONDS: int = int(os.getenv("TASKFORGE_EXTERNAL_AI_RETRY_BACKOFF_SECONDS", os.getenv("TASKFORGE_AI_OLLAMA_RETRY_BACKOFF_SECONDS", "8")))
MAX_JOB_RETRIES: int = int(os.getenv("TASKFORGE_AI_MAX_JOB_RETRIES", "3"))
RETRYABLE_STAGE_DELAY_SECONDS: int = int(os.getenv("TASKFORGE_AI_RETRYABLE_STAGE_DELAY_SECONDS", "45"))
PLANNER_FALLBACK_AFTER_RETRY_COUNT: int = int(os.getenv("TASKFORGE_AI_PLANNER_FALLBACK_AFTER_RETRY_COUNT", "2"))

# ── Quality-over-speed pacing / retry memory ────────
EXTERNAL_AI_MIN_REQUEST_INTERVAL_SECONDS: float = float(os.getenv("TASKFORGE_EXTERNAL_AI_MIN_REQUEST_INTERVAL_SECONDS", "12"))
EXTERNAL_AI_POST_SUCCESS_COOLDOWN_SECONDS: float = float(os.getenv("TASKFORGE_EXTERNAL_AI_POST_SUCCESS_COOLDOWN_SECONDS", "8"))
EXTERNAL_AI_RATE_LIMIT_COOLDOWN_SECONDS: float = float(os.getenv("TASKFORGE_EXTERNAL_AI_RATE_LIMIT_COOLDOWN_SECONDS", "45"))
EXTERNAL_AI_MAX_RATE_LIMIT_COOLDOWN_SECONDS: float = float(os.getenv("TASKFORGE_EXTERNAL_AI_MAX_RATE_LIMIT_COOLDOWN_SECONDS", "180"))
EXTERNAL_AI_RETRY_WITH_RESPONSE_MEMORY: bool = _env_bool("TASKFORGE_EXTERNAL_AI_RETRY_WITH_RESPONSE_MEMORY", True)
EXTERNAL_AI_RESPONSE_MEMORY_MAX_CHARS: int = int(os.getenv("TASKFORGE_EXTERNAL_AI_RESPONSE_MEMORY_MAX_CHARS", "5000"))

# ── Stage budgets ───────────────────────────────────
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

# ── Quality gates defaults ───────────────────────────
MAX_REFERENCE_ASSIGNMENTS: int = int(os.getenv("TASKFORGE_AI_MAX_REFERENCE_ASSIGNMENTS", "20"))
MIN_PUBLIC_TESTS: int = int(os.getenv("TASKFORGE_AI_MIN_PUBLIC_TESTS", "2"))
MIN_HIDDEN_TESTS: int = int(os.getenv("TASKFORGE_AI_MIN_HIDDEN_TESTS", "3"))
MAX_HIDDEN_TESTS: int = int(os.getenv("TASKFORGE_AI_MAX_HIDDEN_TESTS", os.getenv("TASKFORGE_AI_MIN_HIDDEN_TESTS", "3")))
MIN_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_MIN_DESCRIPTION_LEN", "200"))
MAX_REPAIR_ATTEMPTS: int = int(os.getenv("TASKFORGE_AI_REPAIR_ATTEMPTS", "2"))
MAX_REFERENCE_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_REFERENCE_DESCRIPTION_LEN", "260"))
GAP_ANALYSIS_REFERENCE_ASSIGNMENTS: int = int(os.getenv("TASKFORGE_AI_GAP_REFERENCE_ASSIGNMENTS", "8"))
BATCH_PLAN_REFERENCE_ASSIGNMENTS: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_REFERENCE_ASSIGNMENTS", "6"))
BATCH_PLAN_REFERENCE_ASSIGNMENTS_RETRY: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_REFERENCE_ASSIGNMENTS_RETRY", "3"))
BATCH_PLAN_REFERENCE_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_BATCH_PLAN_REFERENCE_DESCRIPTION_LEN", "140"))
GAP_ANALYSIS_REFERENCE_DESCRIPTION_LEN: int = int(os.getenv("TASKFORGE_AI_GAP_REFERENCE_DESCRIPTION_LEN", "160"))

# ── Shared HTTP session ──────────────────────────────
session: requests.Session = requests.Session()
session.headers.update({"X-Internal-Key": API_KEY})
