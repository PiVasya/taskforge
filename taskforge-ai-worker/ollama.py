"""Ollama LLM client."""

import json
import time
from typing import Any, Dict

import requests

from config import OLLAMA_BASE, OLLAMA_MODEL, OLLAMA_TEMPERATURE, OLLAMA_NUM_CTX, TIMEOUT
from log import log


def call_ollama(prompt: str) -> Dict[str, Any]:
    log(f"ollama >>> model={OLLAMA_MODEL} prompt_len={len(prompt)}")
    started = time.time()
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
    log(f"ollama <<< {elapsed_ms}ms response_len={len(raw)}")
    return json.loads(raw)
