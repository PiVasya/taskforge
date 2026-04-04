"""Ollama LLM client."""

import json
import time
from typing import Any, Dict

import requests

from config import (
    OLLAMA_BASE,
    OLLAMA_MODEL,
    OLLAMA_TEMPERATURE,
    OLLAMA_NUM_CTX,
    TIMEOUT,
    OLLAMA_REQUEST_ATTEMPTS,
    OLLAMA_RETRY_BACKOFF_SECONDS,
)
from log import log


def call_ollama(prompt: str) -> Dict[str, Any]:
    log(f"ollama >>> model={OLLAMA_MODEL} prompt_len={len(prompt)} attempts={OLLAMA_REQUEST_ATTEMPTS} timeout={TIMEOUT}s")
    last_error: Exception | None = None
    for attempt in range(1, max(1, OLLAMA_REQUEST_ATTEMPTS) + 1):
        started = time.time()
        try:
            resp = requests.post(
                f"{OLLAMA_BASE}/api/generate",
                json={
                    "model": OLLAMA_MODEL,
                    "prompt": prompt,
                    "stream": False,
                    "options": {
                        "temperature": OLLAMA_TEMPERATURE,
                        "num_ctx": OLLAMA_NUM_CTX,
                    },
                },
                timeout=TIMEOUT,
            )
            elapsed_ms = int((time.time() - started) * 1000)
            resp.raise_for_status()
            data = resp.json()
            raw = (data.get("response") or "").strip()
            if not raw:
                raise RuntimeError("Ollama returned empty response")
            parsed = json.loads(raw)
            log(f"ollama <<< attempt={attempt}/{OLLAMA_REQUEST_ATTEMPTS} {elapsed_ms}ms response_len={len(raw)}")
            return parsed
        except Exception as ex:
            elapsed_ms = int((time.time() - started) * 1000)
            last_error = ex
            log(f"ollama !!! attempt={attempt}/{OLLAMA_REQUEST_ATTEMPTS} failed after {elapsed_ms}ms error={ex}")
            if attempt >= max(1, OLLAMA_REQUEST_ATTEMPTS):
                break
            time.sleep(max(1, OLLAMA_RETRY_BACKOFF_SECONDS) * attempt)
    raise RuntimeError(f"Ollama failed after {OLLAMA_REQUEST_ATTEMPTS} attempt(s): {last_error}")
