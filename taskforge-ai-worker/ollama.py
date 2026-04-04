"""Ollama LLM client."""

import json
import re
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
    OLLAMA_JSON_MODE,
)
from log import log


def _strip_code_fences(text: str) -> str:
    cleaned = text.strip()
    if cleaned.startswith("```"):
        cleaned = re.sub(r"^```(?:json)?\s*", "", cleaned, flags=re.IGNORECASE)
        cleaned = re.sub(r"\s*```$", "", cleaned)
    return cleaned.strip()


def _cleanup_json_candidate(text: str) -> str:
    cleaned = text.strip().replace("\ufeff", "")
    replacements = {
        "“": '"',
        "”": '"',
        "„": '"',
        "«": '"',
        "»": '"',
        "’": "'",
        "‘": "'",
    }
    for src, dst in replacements.items():
        cleaned = cleaned.replace(src, dst)
    cleaned = re.sub(r",\s*([}\]])", r"\1", cleaned)
    return cleaned.strip()


def _extract_balanced_json(text: str) -> str | None:
    starts = [i for i, ch in enumerate(text) if ch in "[{"]
    for start in starts:
        stack: list[str] = []
        in_string = False
        escaped = False
        for idx in range(start, len(text)):
            ch = text[idx]
            if in_string:
                if escaped:
                    escaped = False
                elif ch == "\\":
                    escaped = True
                elif ch == '"':
                    in_string = False
                continue
            if ch == '"':
                in_string = True
                continue
            if ch in "[{":
                stack.append(ch)
            elif ch in "]}":
                if not stack:
                    break
                opener = stack.pop()
                if (opener, ch) not in {("{", "}"), ("[", "]")}:
                    break
                if not stack:
                    return text[start:idx + 1]
    return None


def _parse_json_response(raw: str) -> Dict[str, Any]:
    candidates: list[tuple[str, str]] = []
    stripped = _strip_code_fences(raw)
    candidates.append(("raw", raw.strip()))
    if stripped != raw.strip():
        candidates.append(("codefence", stripped))
    extracted = _extract_balanced_json(stripped)
    if extracted:
        candidates.append(("extracted", extracted))
    seen: set[str] = set()
    last_error: Exception | None = None
    for label, candidate in candidates:
        for variant_label, variant in ((label, candidate), (f"{label}-clean", _cleanup_json_candidate(candidate))):
            variant = variant.strip()
            if not variant or variant in seen:
                continue
            seen.add(variant)
            try:
                parsed = json.loads(variant)
                if not isinstance(parsed, dict):
                    raise RuntimeError(f"JSON root is {type(parsed).__name__}, expected object")
                if variant_label != "raw":
                    log(f"ollama json repaired via {variant_label} response_len={len(raw)} candidate_len={len(variant)}")
                return parsed
            except Exception as ex:  # pragma: no cover - best effort repair path
                last_error = ex
    raise RuntimeError(f"Could not parse Ollama JSON response: {last_error}")


def call_ollama(prompt: str) -> Dict[str, Any]:
    log(f"ollama >>> model={OLLAMA_MODEL} prompt_len={len(prompt)} attempts={OLLAMA_REQUEST_ATTEMPTS} timeout={TIMEOUT}s")
    last_error: Exception | None = None
    for attempt in range(1, max(1, OLLAMA_REQUEST_ATTEMPTS) + 1):
        started = time.time()
        try:
            body: Dict[str, Any] = {
                "model": OLLAMA_MODEL,
                "prompt": prompt,
                "stream": False,
                "options": {
                    "temperature": OLLAMA_TEMPERATURE,
                    "num_ctx": OLLAMA_NUM_CTX,
                },
            }
            if OLLAMA_JSON_MODE:
                body["format"] = "json"
            resp = requests.post(
                f"{OLLAMA_BASE}/api/generate",
                json=body,
                timeout=TIMEOUT,
            )
            elapsed_ms = int((time.time() - started) * 1000)
            resp.raise_for_status()
            data = resp.json()
            raw = (data.get("response") or "").strip()
            if not raw:
                raise RuntimeError("Ollama returned empty response")
            parsed = _parse_json_response(raw)
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
