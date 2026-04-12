"""Ollama LLM client with stage-aware budgets and JSON repair."""

import json
import re
import time
from typing import Any, Dict, Iterable

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
from log import log, log_event, preview_text, INCLUDE_PROMPTS, INCLUDE_RESPONSES, PROMPT_PREVIEW_CHARS, RESPONSE_PREVIEW_CHARS


class OllamaCallConfig:
    def __init__(
        self,
        *,
        stage: str = "generic",
        timeout: int | None = None,
        attempts: int | None = None,
        num_predict: int | None = None,
        temperature: float | None = None,
        json_mode: bool | None = None,
        required_keys: Iterable[str] | None = None,
        preferred_keys: Iterable[str] | None = None,
    ) -> None:
        self.stage = stage
        self.timeout = timeout or TIMEOUT
        self.attempts = attempts or OLLAMA_REQUEST_ATTEMPTS
        self.num_predict = num_predict
        self.temperature = OLLAMA_TEMPERATURE if temperature is None else temperature
        self.json_mode = OLLAMA_JSON_MODE if json_mode is None else json_mode
        self.required_keys = [str(x) for x in (required_keys or []) if str(x).strip()]
        self.preferred_keys = [str(x) for x in (preferred_keys or []) if str(x).strip()]


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


def _extract_balanced_json_candidates(text: str) -> list[str]:
    candidates: list[str] = []
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
                    candidates.append(text[start:idx + 1])
                    break
    seen: set[str] = set()
    unique: list[str] = []
    for item in candidates:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return unique


def _score_parsed_candidate(parsed: Dict[str, Any], variant: str, *, required_keys: list[str], preferred_keys: list[str]) -> tuple[int, int, int, int]:
    key_set = {str(k) for k in parsed.keys()}
    required_hit = sum(1 for key in required_keys if key in key_set)
    preferred_hit = sum(1 for key in preferred_keys if key in key_set)
    structural_bonus = 0
    if isinstance(parsed.get("plan"), dict) and isinstance(parsed["plan"].get("tasks"), list):
        structural_bonus += 8 + len(parsed["plan"].get("tasks") or [])
    if isinstance(parsed.get("gapAnalysis"), dict):
        structural_bonus += 6 + len(parsed["gapAnalysis"].keys())
    if isinstance(parsed.get("courseProfile"), dict):
        structural_bonus += 4 + len(parsed["courseProfile"].keys())
    return (required_hit, preferred_hit, structural_bonus + len(parsed), len(variant))


def _parse_json_response(raw: str, *, required_keys: Iterable[str] | None = None, preferred_keys: Iterable[str] | None = None) -> Dict[str, Any]:
    required = [str(x) for x in (required_keys or []) if str(x).strip()]
    preferred = [str(x) for x in (preferred_keys or []) if str(x).strip()]
    candidates: list[tuple[str, str]] = []
    stripped = _strip_code_fences(raw)
    candidates.append(("raw", raw.strip()))
    if stripped != raw.strip():
        candidates.append(("codefence", stripped))
    for idx, item in enumerate(sorted(_extract_balanced_json_candidates(stripped), key=len, reverse=True), start=1):
        candidates.append((f"extracted-{idx}", item))
    seen_variants: set[str] = set()
    parsed_candidates: list[tuple[tuple[int, int, int, int], str, Dict[str, Any], str]] = []
    last_error: Exception | None = None
    for label, candidate in candidates:
        for variant_label, variant in ((label, candidate), (f"{label}-clean", _cleanup_json_candidate(candidate))):
            variant = variant.strip()
            if not variant or variant in seen_variants:
                continue
            seen_variants.add(variant)
            try:
                parsed = json.loads(variant)
                if not isinstance(parsed, dict):
                    raise RuntimeError(f"JSON root is {type(parsed).__name__}, expected object")
                score = _score_parsed_candidate(parsed, variant, required_keys=required, preferred_keys=preferred)
                parsed_candidates.append((score, variant_label, parsed, variant))
            except Exception as ex:
                last_error = ex
    if not parsed_candidates:
        raise RuntimeError(f"Could not parse Ollama JSON response: {last_error}")
    parsed_candidates.sort(key=lambda item: item[0], reverse=True)
    best_score, best_label, best_parsed, best_variant = parsed_candidates[0]
    if best_label != "raw":
        log(f"ollama json repaired via {best_label} response_len={len(raw)} candidate_len={len(best_variant)} score={best_score}")
    return best_parsed


def call_ollama(prompt: str, config: OllamaCallConfig | None = None) -> Dict[str, Any]:
    cfg = config or OllamaCallConfig()
    log(
        f"ollama >>> stage={cfg.stage} model={OLLAMA_MODEL} prompt_len={len(prompt)} "
        f"attempts={cfg.attempts} timeout={cfg.timeout}s format={'json' if cfg.json_mode else 'text'} "
        f"num_predict={cfg.num_predict or '-'}"
    )
    last_error: Exception | None = None
    for attempt in range(1, max(1, cfg.attempts) + 1):
        started = time.time()
        try:
            body: Dict[str, Any] = {
                "model": OLLAMA_MODEL,
                "prompt": prompt,
                "stream": False,
                "options": {
                    "temperature": cfg.temperature,
                    "num_ctx": OLLAMA_NUM_CTX,
                },
            }
            if cfg.num_predict is not None:
                body["options"]["num_predict"] = cfg.num_predict
            if cfg.json_mode:
                body["format"] = "json"
            resp = requests.post(
                f"{OLLAMA_BASE}/api/generate",
                json=body,
                timeout=cfg.timeout,
            )
            elapsed_ms = int((time.time() - started) * 1000)
            resp.raise_for_status()
            data = resp.json()
            raw = (data.get("response") or "").strip()
            if not raw:
                raise RuntimeError("Ollama returned empty response")
            parsed = _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if cfg.json_mode else {"text": raw}
            log(f"ollama <<< stage={cfg.stage} attempt={attempt}/{cfg.attempts} {elapsed_ms}ms response_len={len(raw)}")
            return parsed
        except Exception as ex:
            elapsed_ms = int((time.time() - started) * 1000)
            last_error = ex
            log(f"ollama !!! stage={cfg.stage} attempt={attempt}/{cfg.attempts} failed after {elapsed_ms}ms error={ex}")
            if attempt >= max(1, cfg.attempts):
                break
            time.sleep(max(1, OLLAMA_RETRY_BACKOFF_SECONDS) * attempt)
    raise RuntimeError(f"Ollama failed after {cfg.attempts} attempt(s): {last_error}")
