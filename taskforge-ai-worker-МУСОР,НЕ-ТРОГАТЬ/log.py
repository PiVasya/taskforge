"""Logging setup for the AI worker with console + rotating text file + JSONL event log."""

from __future__ import annotations

import json
import logging
import os
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any, Mapping


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() not in {"0", "false", "no", "off", ""}


LOG_LEVEL = (os.getenv("TASKFORGE_AI_LOG_LEVEL", "INFO") or "INFO").upper()
LOG_DIR = Path(os.getenv("TASKFORGE_AI_LOG_DIR", "/app/logs")).expanduser()
TEXT_LOG_FILE = Path(os.getenv("TASKFORGE_AI_LOG_FILE", str(LOG_DIR / "worker.log"))).expanduser()
JSON_LOG_FILE = Path(os.getenv("TASKFORGE_AI_JSON_LOG_FILE", str(LOG_DIR / "worker.jsonl"))).expanduser()
LOG_MAX_BYTES = int(os.getenv("TASKFORGE_AI_LOG_MAX_BYTES", "10485760"))
LOG_BACKUP_COUNT = int(os.getenv("TASKFORGE_AI_LOG_BACKUP_COUNT", "5"))
CONSOLE_LEVEL = (os.getenv("TASKFORGE_AI_CONSOLE_LOG_LEVEL", LOG_LEVEL) or LOG_LEVEL).upper()
INCLUDE_PROMPTS = _env_bool("TASKFORGE_AI_LOG_INCLUDE_PROMPTS", True)
INCLUDE_RESPONSES = _env_bool("TASKFORGE_AI_LOG_INCLUDE_RESPONSES", True)
PROMPT_PREVIEW_CHARS = int(os.getenv("TASKFORGE_AI_LOG_PROMPT_PREVIEW_CHARS", "900"))
RESPONSE_PREVIEW_CHARS = int(os.getenv("TASKFORGE_AI_LOG_RESPONSE_PREVIEW_CHARS", "900"))
PAYLOAD_PREVIEW_CHARS = int(os.getenv("TASKFORGE_AI_LOG_PAYLOAD_PREVIEW_CHARS", "600"))

LOG_DIR.mkdir(parents=True, exist_ok=True)
TEXT_LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
JSON_LOG_FILE.parent.mkdir(parents=True, exist_ok=True)

logger: logging.Logger = logging.getLogger("taskforge-ai-worker")
logger.setLevel(getattr(logging, LOG_LEVEL, logging.INFO))
logger.handlers.clear()
logger.propagate = False

console_handler = logging.StreamHandler()
console_handler.setLevel(getattr(logging, CONSOLE_LEVEL, logging.INFO))
console_handler.setFormatter(logging.Formatter(
    fmt="%(asctime)s [worker] %(levelname)s %(message)s",
    datefmt="%H:%M:%S",
))
logger.addHandler(console_handler)

file_handler = RotatingFileHandler(TEXT_LOG_FILE, maxBytes=LOG_MAX_BYTES, backupCount=LOG_BACKUP_COUNT, encoding="utf-8")
file_handler.setLevel(getattr(logging, LOG_LEVEL, logging.INFO))
file_handler.setFormatter(logging.Formatter(
    fmt="%(asctime)s [worker] %(levelname)s pid=%(process)d %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
))
logger.addHandler(file_handler)

json_logger: logging.Logger = logging.getLogger("taskforge-ai-worker-json")
json_logger.setLevel(logging.INFO)
json_logger.handlers.clear()
json_logger.propagate = False
json_handler = RotatingFileHandler(JSON_LOG_FILE, maxBytes=LOG_MAX_BYTES, backupCount=LOG_BACKUP_COUNT, encoding="utf-8")
json_handler.setFormatter(logging.Formatter("%(message)s"))
json_logger.addHandler(json_handler)


def preview_text(value: Any, limit: int = 200) -> str:
    if value is None:
        return "-"
    text = str(value).replace("\n", " ").replace("\r", " ").strip()
    if not text:
        return "-"
    if len(text) <= limit:
        return text
    return text[: max(0, limit - 3)] + "..."


def to_jsonable(value: Any, *, limit: int = 400) -> Any:
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return preview_text(value, limit)
    if isinstance(value, Mapping):
        return {str(k): to_jsonable(v, limit=limit) for k, v in list(value.items())[:30]}
    if isinstance(value, (list, tuple, set)):
        seq = list(value)
        return [to_jsonable(v, limit=limit) for v in seq[:20]]
    return preview_text(value, limit)


def _render_part(value: Any) -> str:
    if isinstance(value, Mapping):
        pieces = []
        for key, item in value.items():
            pieces.append(f"{key}={preview_text(item, 220)}")
        return " ".join(pieces)
    return str(value)


def write_json_event(event: str, **fields: Any) -> None:
    payload = {"event": event}
    payload.update({k: to_jsonable(v, limit=PAYLOAD_PREVIEW_CHARS) for k, v in fields.items()})
    json_logger.info(json.dumps(payload, ensure_ascii=False, sort_keys=True))


def log(*parts: Any) -> None:
    logger.info(" ".join(_render_part(p) for p in parts if p is not None))


def log_debug(*parts: Any) -> None:
    logger.debug(" ".join(_render_part(p) for p in parts if p is not None))


def log_event(event: str, /, level: str = "info", **fields: Any) -> None:
    ordered = []
    for key, value in fields.items():
        if value is None:
            continue
        ordered.append(f"{key}={preview_text(value, 260)}")
    message = f"{event} | " + " | ".join(ordered) if ordered else event
    log_fn = getattr(logger, level.lower(), logger.info)
    log_fn(message)
    write_json_event(event, level=level.lower(), **fields)
