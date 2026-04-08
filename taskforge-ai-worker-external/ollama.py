"""Stage-aware LLM client.

Historical name preserved for compatibility: worker imports ``call_ollama`` and
``OllamaCallConfig``. In the external worker this module now routes requests to
OpenAI-compatible providers (including OpenRouter), Anthropic, or Ollama based on
configuration.
"""

import json
import re
import time
from typing import Any, Dict, Iterable

import requests

from config import (
    EXTERNAL_AI_PROVIDER,
    ACTIVE_PROVIDER,
    ACTIVE_MODEL,
    EXTERNAL_AI_BASE_URL,
    EXTERNAL_AI_API_KEY,
    EXTERNAL_AI_MODEL,
    EXTERNAL_AI_TEMPERATURE,
    EXTERNAL_AI_JSON_MODE,
    EXTERNAL_AI_REQUEST_ATTEMPTS,
    EXTERNAL_AI_RETRY_BACKOFF_SECONDS,
    EXTERNAL_AI_EXTRA_HEADERS,
    EXTERNAL_AI_ANTHROPIC_VERSION,
    EXTERNAL_AI_TOP_P,
    OLLAMA_BASE,
    OLLAMA_MODEL,
    OLLAMA_NUM_CTX,
    OLLAMA_TEMPERATURE,
    OLLAMA_JSON_MODE,
    TIMEOUT,
    OLLAMA_REQUEST_ATTEMPTS,
    OLLAMA_RETRY_BACKOFF_SECONDS,
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
        default_attempts = EXTERNAL_AI_REQUEST_ATTEMPTS if ACTIVE_PROVIDER != "ollama" else OLLAMA_REQUEST_ATTEMPTS
        self.attempts = attempts or default_attempts
        self.num_predict = num_predict
        default_temp = EXTERNAL_AI_TEMPERATURE if ACTIVE_PROVIDER != "ollama" else OLLAMA_TEMPERATURE
        default_json = EXTERNAL_AI_JSON_MODE if ACTIVE_PROVIDER != "ollama" else OLLAMA_JSON_MODE
        self.temperature = default_temp if temperature is None else temperature
        self.json_mode = default_json if json_mode is None else json_mode
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
        raise RuntimeError(f"Could not parse JSON response: {last_error}")
    parsed_candidates.sort(key=lambda item: item[0], reverse=True)
    best_score, best_label, best_parsed, best_variant = parsed_candidates[0]
    if best_label != "raw":
        prefix = "ollama" if ACTIVE_PROVIDER == "ollama" else "external-llm"
        log_event('llm-json-repaired', provider=ACTIVE_PROVIDER, strategy=best_label, raw_len=len(raw), candidate_len=len(best_variant), score=best_score, candidate_preview=(preview_text(best_variant, RESPONSE_PREVIEW_CHARS) if INCLUDE_RESPONSES else None))
    return best_parsed


def _http_error(resp: requests.Response) -> RuntimeError:
    text = (resp.text or "").strip()
    snippet = text[:600]
    return RuntimeError(f"HTTP {resp.status_code}: {snippet}")


def _extract_openai_content(data: Dict[str, Any]) -> str:
    choices = data.get("choices") if isinstance(data.get("choices"), list) else []
    if not choices:
        return ""
    message = choices[0].get("message") if isinstance(choices[0], dict) else None
    if not isinstance(message, dict):
        return ""
    content = message.get("content")
    if isinstance(content, str):
        return content.strip()
    if isinstance(content, list):
        parts = []
        for item in content:
            if isinstance(item, dict) and item.get("type") == "text":
                parts.append(str(item.get("text") or ""))
        return "\n".join(p for p in parts if p).strip()
    return ""


def _call_openai_compatible(prompt: str, cfg: OllamaCallConfig) -> Dict[str, Any]:
    if not EXTERNAL_AI_API_KEY:
        raise RuntimeError("TASKFORGE_EXTERNAL_AI_API_KEY is empty")
    headers = {
        "Authorization": f"Bearer {EXTERNAL_AI_API_KEY}",
        "Content-Type": "application/json",
    }
    headers.update(EXTERNAL_AI_EXTRA_HEADERS)
    body: Dict[str, Any] = {
        "model": EXTERNAL_AI_MODEL,
        "messages": [{"role": "user", "content": prompt}],
        "temperature": cfg.temperature,
        "top_p": EXTERNAL_AI_TOP_P,
    }
    if cfg.num_predict is not None:
        body["max_tokens"] = cfg.num_predict
    used_json_hint = False
    if cfg.json_mode:
        body["response_format"] = {"type": "json_object"}
        used_json_hint = True
    resp = requests.post(f"{EXTERNAL_AI_BASE_URL}/chat/completions", json=body, headers=headers, timeout=cfg.timeout)
    if resp.status_code >= 400 and used_json_hint:
        log_event('llm-json-hint-rejected', level='warning', provider='openai_compatible', stage=cfg.stage, response_preview=preview_text(resp.text, RESPONSE_PREVIEW_CHARS))
        body.pop("response_format", None)
        resp = requests.post(f"{EXTERNAL_AI_BASE_URL}/chat/completions", json=body, headers=headers, timeout=cfg.timeout)
    if resp.status_code >= 400:
        raise _http_error(resp)
    data = resp.json()
    raw = _extract_openai_content(data)
    if not raw:
        raise RuntimeError("OpenAI-compatible provider returned empty content")
    return _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if cfg.json_mode else {"text": raw}


def _call_anthropic(prompt: str, cfg: OllamaCallConfig) -> Dict[str, Any]:
    if not EXTERNAL_AI_API_KEY:
        raise RuntimeError("TASKFORGE_EXTERNAL_AI_API_KEY is empty")
    headers = {
        "x-api-key": EXTERNAL_AI_API_KEY,
        "anthropic-version": EXTERNAL_AI_ANTHROPIC_VERSION,
        "content-type": "application/json",
    }
    headers.update(EXTERNAL_AI_EXTRA_HEADERS)
    body: Dict[str, Any] = {
        "model": EXTERNAL_AI_MODEL,
        "max_tokens": cfg.num_predict or 1024,
        "temperature": cfg.temperature,
        "messages": [{"role": "user", "content": prompt}],
    }
    resp = requests.post(f"{EXTERNAL_AI_BASE_URL}/messages", json=body, headers=headers, timeout=cfg.timeout)
    if resp.status_code >= 400:
        raise _http_error(resp)
    data = resp.json()
    content = data.get("content") if isinstance(data.get("content"), list) else []
    raw_parts = []
    for item in content:
        if isinstance(item, dict) and item.get("type") == "text":
            raw_parts.append(str(item.get("text") or ""))
    raw = "\n".join(x for x in raw_parts if x).strip()
    if not raw:
        raise RuntimeError("Anthropic returned empty content")
    return _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if cfg.json_mode else {"text": raw}


def _call_ollama(prompt: str, cfg: OllamaCallConfig) -> Dict[str, Any]:
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
    resp = requests.post(f"{OLLAMA_BASE}/api/generate", json=body, timeout=cfg.timeout)
    if resp.status_code >= 400:
        raise _http_error(resp)
    data = resp.json()
    raw = (data.get("response") or "").strip()
    if not raw:
        raise RuntimeError("Ollama returned empty response")
    return _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if cfg.json_mode else {"text": raw}


def call_ollama(prompt: str, config: OllamaCallConfig | None = None) -> Dict[str, Any]:
    cfg = config or OllamaCallConfig()
    provider = ACTIVE_PROVIDER or "openai_compatible"
    if provider == "ollama":
        model = OLLAMA_MODEL
        prefix = "ollama"
        attempts = cfg.attempts or OLLAMA_REQUEST_ATTEMPTS
        backoff = OLLAMA_RETRY_BACKOFF_SECONDS
    else:
        model = ACTIVE_MODEL
        prefix = "external-llm"
        attempts = cfg.attempts or EXTERNAL_AI_REQUEST_ATTEMPTS
        backoff = EXTERNAL_AI_RETRY_BACKOFF_SECONDS
    log_event('llm-request-start', provider=provider, model=model, stage=cfg.stage, prompt_len=len(prompt), attempts=attempts, timeout_seconds=cfg.timeout, format=('json' if cfg.json_mode else 'text'), num_predict=(cfg.num_predict or '-'), prompt_preview=(preview_text(prompt, PROMPT_PREVIEW_CHARS) if INCLUDE_PROMPTS else None))
    last_error: Exception | None = None
    for attempt in range(1, max(1, attempts) + 1):
        started = time.time()
        try:
            if provider == "ollama":
                parsed = _call_ollama(prompt, cfg)
            elif provider == "anthropic":
                parsed = _call_anthropic(prompt, cfg)
            else:
                parsed = _call_openai_compatible(prompt, cfg)
            elapsed_ms = int((time.time() - started) * 1000)
            log_event('llm-request-success', provider=provider, model=model, stage=cfg.stage, attempt=f'{attempt}/{attempts}', elapsed_ms=elapsed_ms, response_len=len(json.dumps(parsed, ensure_ascii=False)), response_preview=(preview_text(parsed, RESPONSE_PREVIEW_CHARS) if INCLUDE_RESPONSES else None))
            return parsed
        except Exception as ex:
            elapsed_ms = int((time.time() - started) * 1000)
            last_error = ex
            log_event('llm-request-failed', level='warning', provider=provider, model=model, stage=cfg.stage, attempt=f'{attempt}/{attempts}', elapsed_ms=elapsed_ms, error=str(ex))
            if attempt >= max(1, attempts):
                break
            time.sleep(max(1, backoff) * attempt)
    provider_title = "Ollama" if provider == "ollama" else "External LLM"
    raise RuntimeError(f"{provider_title} failed after {attempts} attempt(s): {last_error}")
