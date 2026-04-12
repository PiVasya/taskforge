"""Stage-aware LLM client for the external AI worker.

Primary import path is now ``llm_client``. Legacy ``ollama`` module remains as a
compatibility shim so older imports keep working while the external worker
continues moving away from Ollama-first naming.
"""

from __future__ import annotations

import json
import re
import time
from typing import Any, Dict, Iterable

import requests

from config import (
    ACTIVE_MODEL,
    ACTIVE_PROVIDER,
    ASSISTANT_PREFILL,
    EXTERNAL_AI_ANTHROPIC_VERSION,
    EXTERNAL_AI_API_KEY,
    EXTERNAL_AI_BASE_URL,
    EXTERNAL_AI_EXTRA_HEADERS,
    EXTERNAL_AI_JSON_MODE,
    EXTERNAL_AI_MODEL,
    EXTERNAL_AI_REQUEST_ATTEMPTS,
    EXTERNAL_AI_RETRY_BACKOFF_SECONDS,
    EXTERNAL_AI_TEMPERATURE,
    EXTERNAL_AI_TOP_P,
    OLLAMA_BASE,
    OLLAMA_JSON_MODE,
    OLLAMA_MODEL,
    OLLAMA_NUM_CTX,
    OLLAMA_REQUEST_ATTEMPTS,
    OLLAMA_RETRY_BACKOFF_SECONDS,
    OLLAMA_TEMPERATURE,
    OPENROUTER_ALLOW_FALLBACKS,
    OPENROUTER_APP_TITLE,
    OPENROUTER_APP_URL,
    OPENROUTER_CATEGORIES,
    OPENROUTER_DATA_COLLECTION,
    OPENROUTER_PLUGINS,
    OPENROUTER_PROVIDER_ORDER,
    OPENROUTER_REQUIRE_PARAMETERS,
    OPENROUTER_USAGE_TELEMETRY,
    OPENROUTER_ZDR,
    TIMEOUT,
)
from log import INCLUDE_PROMPTS, INCLUDE_RESPONSES, PROMPT_PREVIEW_CHARS, RESPONSE_PREVIEW_CHARS, log_event, preview_text


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
        json_schema: Dict[str, Any] | None = None,
        routing: Dict[str, Any] | None = None,
        plugins: list[dict[str, Any]] | None = None,
        assistant_prefill: bool | None = None,
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
        self.json_schema = json_schema if isinstance(json_schema, dict) else None
        self.routing = routing if isinstance(routing, dict) else None
        self.plugins = list(plugins or [])
        self.assistant_prefill = ASSISTANT_PREFILL if assistant_prefill is None else bool(assistant_prefill)


def _is_openrouter() -> bool:
    return "openrouter.ai" in (EXTERNAL_AI_BASE_URL or "").lower()


def _strip_code_fences(text: str) -> str:
    cleaned = text.strip()
    if cleaned.startswith("```"):
        cleaned = re.sub(r"^```(?:json)?\s*", "", cleaned, flags=re.IGNORECASE)
        cleaned = re.sub(r"\s*```$", "", cleaned)
    return cleaned.strip()


def _cleanup_json_candidate(text: str) -> str:
    cleaned = text.strip().replace("\ufeff", "")
    replacements = {"“": '"', "”": '"', "„": '"', "«": '"', "»": '"', "’": "'", "‘": "'"}
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
    if isinstance(parsed.get("actions"), list):
        structural_bonus += 4 + len(parsed.get("actions") or [])
    if isinstance(parsed.get("draft"), dict):
        structural_bonus += 6 + len(parsed.get("draft") or {})
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
        log_event('llm-json-repaired', provider=ACTIVE_PROVIDER, strategy=best_label, raw_len=len(raw), candidate_len=len(best_variant), score=best_score, candidate_preview=(preview_text(best_variant, RESPONSE_PREVIEW_CHARS) if INCLUDE_RESPONSES else None))
    return best_parsed


def _http_error(resp: requests.Response) -> RuntimeError:
    text = (resp.text or "").strip()
    return RuntimeError(f"HTTP {resp.status_code}: {text[:600]}")


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
        return "\n".join(x for x in parts if x).strip()
    return ""


def _extract_usage_meta(data: Dict[str, Any], provider: str, fallback_model: str) -> Dict[str, Any]:
    usage = data.get("usage") if isinstance(data.get("usage"), dict) else {}
    prompt_tokens = usage.get("prompt_tokens") or usage.get("input_tokens") or usage.get("promptTokens")
    completion_tokens = usage.get("completion_tokens") or usage.get("output_tokens") or usage.get("completionTokens")
    total_tokens = usage.get("total_tokens") or usage.get("totalTokens")
    if total_tokens is None and (prompt_tokens is not None or completion_tokens is not None):
        try:
            total_tokens = int(prompt_tokens or 0) + int(completion_tokens or 0)
        except Exception:
            total_tokens = None
    cost = usage.get("cost") or data.get("cost") or data.get("total_cost") or usage.get("total_cost")
    provider_name = data.get("provider") or data.get("provider_name") or provider
    response_model = data.get("model") or fallback_model
    meta: Dict[str, Any] = {
        "provider": provider_name,
        "model": response_model,
        "promptTokens": int(prompt_tokens) if prompt_tokens is not None else None,
        "completionTokens": int(completion_tokens) if completion_tokens is not None else None,
        "totalTokens": int(total_tokens) if total_tokens is not None else None,
        "cost": float(cost) if cost is not None else None,
    }
    return {k: v for k, v in meta.items() if v is not None}


def _with_llm_meta(result: Dict[str, Any], meta: Dict[str, Any] | None) -> Dict[str, Any]:
    if isinstance(result, dict) and meta:
        result["__llmMeta"] = meta
    return result


def _openrouter_provider_payload(cfg: OllamaCallConfig) -> Dict[str, Any]:
    provider = dict(cfg.routing or {})
    if OPENROUTER_PROVIDER_ORDER and "order" not in provider:
        provider["order"] = OPENROUTER_PROVIDER_ORDER
    provider.setdefault("allow_fallbacks", OPENROUTER_ALLOW_FALLBACKS)
    provider["require_parameters"] = True if cfg.json_schema else provider.get("require_parameters", OPENROUTER_REQUIRE_PARAMETERS)
    if OPENROUTER_DATA_COLLECTION:
        provider.setdefault("data_collection", OPENROUTER_DATA_COLLECTION)
    if OPENROUTER_ZDR:
        provider.setdefault("zdr", True)
    return provider


def _openrouter_plugins(cfg: OllamaCallConfig) -> list[dict[str, Any]]:
    if cfg.plugins:
        return cfg.plugins
    return [{"id": x} for x in OPENROUTER_PLUGINS]


def _call_openai_compatible(prompt: str, cfg: OllamaCallConfig) -> tuple[Dict[str, Any], Dict[str, Any]]:
    if not EXTERNAL_AI_API_KEY:
        raise RuntimeError("TASKFORGE_EXTERNAL_AI_API_KEY is empty")
    headers = {"Authorization": f"Bearer {EXTERNAL_AI_API_KEY}", "Content-Type": "application/json"}
    headers.update(EXTERNAL_AI_EXTRA_HEADERS)
    body: Dict[str, Any] = {
        "model": EXTERNAL_AI_MODEL,
        "messages": [{"role": "user", "content": prompt}],
        "temperature": cfg.temperature,
        "top_p": EXTERNAL_AI_TOP_P,
    }
    if cfg.num_predict is not None:
        body["max_tokens"] = cfg.num_predict
    if cfg.assistant_prefill and (cfg.json_mode or cfg.json_schema):
        body["messages"].append({"role": "assistant", "content": "{"})
    if _is_openrouter():
        if OPENROUTER_APP_URL:
            headers.setdefault("HTTP-Referer", OPENROUTER_APP_URL)
        if OPENROUTER_APP_TITLE:
            headers.setdefault("X-OpenRouter-Title", OPENROUTER_APP_TITLE)
        if OPENROUTER_CATEGORIES:
            headers.setdefault("X-OpenRouter-Categories", OPENROUTER_CATEGORIES)
        body["provider"] = _openrouter_provider_payload(cfg)
        plugins = _openrouter_plugins(cfg)
        if plugins:
            body["plugins"] = plugins
    used_json_hint = False
    if cfg.json_schema:
        body["response_format"] = {"type": "json_schema", "json_schema": cfg.json_schema}
        used_json_hint = True
    elif cfg.json_mode:
        body["response_format"] = {"type": "json_object"}
        used_json_hint = True

    resp = requests.post(f"{EXTERNAL_AI_BASE_URL}/chat/completions", json=body, headers=headers, timeout=cfg.timeout)
    if resp.status_code >= 400 and used_json_hint:
        log_event('llm-json-hint-rejected', level='warning', provider=('openrouter' if _is_openrouter() else 'openai_compatible'), stage=cfg.stage, response_preview=preview_text(resp.text, RESPONSE_PREVIEW_CHARS))
        if cfg.json_schema and cfg.json_mode:
            body["response_format"] = {"type": "json_object"}
        else:
            body.pop("response_format", None)
        if _is_openrouter() and isinstance(body.get("provider"), dict):
            body["provider"]["require_parameters"] = False
        resp = requests.post(f"{EXTERNAL_AI_BASE_URL}/chat/completions", json=body, headers=headers, timeout=cfg.timeout)
    if resp.status_code >= 400:
        raise _http_error(resp)
    data = resp.json()
    raw = _extract_openai_content(data)
    if not raw:
        raise RuntimeError("OpenAI-compatible provider returned empty content")
    parsed = _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if (cfg.json_mode or cfg.json_schema) else {"text": raw}
    meta = _extract_usage_meta(data, "openrouter" if _is_openrouter() else "openai_compatible", EXTERNAL_AI_MODEL) if OPENROUTER_USAGE_TELEMETRY else {}
    return _with_llm_meta(parsed, meta), meta


def _call_anthropic(prompt: str, cfg: OllamaCallConfig) -> tuple[Dict[str, Any], Dict[str, Any]]:
    if not EXTERNAL_AI_API_KEY:
        raise RuntimeError("TASKFORGE_EXTERNAL_AI_API_KEY is empty")
    headers = {"x-api-key": EXTERNAL_AI_API_KEY, "anthropic-version": EXTERNAL_AI_ANTHROPIC_VERSION, "content-type": "application/json"}
    headers.update(EXTERNAL_AI_EXTRA_HEADERS)
    body: Dict[str, Any] = {"model": EXTERNAL_AI_MODEL, "max_tokens": cfg.num_predict or 1024, "temperature": cfg.temperature, "messages": [{"role": "user", "content": prompt}]}
    resp = requests.post(f"{EXTERNAL_AI_BASE_URL}/messages", json=body, headers=headers, timeout=cfg.timeout)
    if resp.status_code >= 400:
        raise _http_error(resp)
    data = resp.json()
    content = data.get("content") if isinstance(data.get("content"), list) else []
    raw = "\n".join(str(item.get("text") or "") for item in content if isinstance(item, dict) and item.get("type") == "text").strip()
    if not raw:
        raise RuntimeError("Anthropic returned empty content")
    parsed = _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if (cfg.json_mode or cfg.json_schema) else {"text": raw}
    return _with_llm_meta(parsed, {}), {}


def _call_ollama(prompt: str, cfg: OllamaCallConfig) -> tuple[Dict[str, Any], Dict[str, Any]]:
    body: Dict[str, Any] = {"model": OLLAMA_MODEL, "prompt": prompt, "stream": False, "options": {"temperature": cfg.temperature, "num_ctx": OLLAMA_NUM_CTX}}
    if cfg.num_predict is not None:
        body["options"]["num_predict"] = cfg.num_predict
    if cfg.json_mode or cfg.json_schema:
        body["format"] = "json"
    resp = requests.post(f"{OLLAMA_BASE}/api/generate", json=body, timeout=cfg.timeout)
    if resp.status_code >= 400:
        raise _http_error(resp)
    data = resp.json()
    raw = (data.get("response") or "").strip()
    if not raw:
        raise RuntimeError("Ollama returned empty response")
    parsed = _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if (cfg.json_mode or cfg.json_schema) else {"text": raw}
    meta = {"provider": "ollama", "model": OLLAMA_MODEL, "totalTokens": data.get("eval_count")}
    meta = {k: v for k, v in meta.items() if v is not None}
    return _with_llm_meta(parsed, meta), meta


def call_llm(prompt: str, config: OllamaCallConfig | None = None) -> Dict[str, Any]:
    cfg = config or OllamaCallConfig()
    provider = ACTIVE_PROVIDER or "openai_compatible"
    if provider == "ollama":
        model = OLLAMA_MODEL
        attempts = cfg.attempts or OLLAMA_REQUEST_ATTEMPTS
        backoff = OLLAMA_RETRY_BACKOFF_SECONDS
    else:
        model = ACTIVE_MODEL
        attempts = cfg.attempts or EXTERNAL_AI_REQUEST_ATTEMPTS
        backoff = EXTERNAL_AI_RETRY_BACKOFF_SECONDS
    fmt = 'json-schema' if cfg.json_schema else ('json' if cfg.json_mode else 'text')
    log_event('llm-request-start', provider=provider, model=model, stage=cfg.stage, prompt_len=len(prompt), attempts=attempts, timeout_seconds=cfg.timeout, format=fmt, num_predict=(cfg.num_predict or '-'), structured=bool(cfg.json_schema), routing=((cfg.routing or {}).get('order') if isinstance(cfg.routing, dict) else None), plugins=([p.get('id') for p in (cfg.plugins or []) if isinstance(p, dict) and p.get('id')] or None), prompt_preview=(preview_text(prompt, PROMPT_PREVIEW_CHARS) if INCLUDE_PROMPTS else None))
    last_error: Exception | None = None
    for attempt in range(1, max(1, attempts) + 1):
        started = time.time()
        try:
            if provider == "ollama":
                parsed, meta = _call_ollama(prompt, cfg)
            elif provider == "anthropic":
                parsed, meta = _call_anthropic(prompt, cfg)
            else:
                parsed, meta = _call_openai_compatible(prompt, cfg)
            elapsed_ms = int((time.time() - started) * 1000)
            log_event('llm-request-success', provider=provider, model=model, stage=cfg.stage, attempt=f'{attempt}/{attempts}', elapsed_ms=elapsed_ms, response_len=len(json.dumps(parsed, ensure_ascii=False)), prompt_tokens=(meta.get("promptTokens") if isinstance(meta, dict) else None), completion_tokens=(meta.get("completionTokens") if isinstance(meta, dict) else None), total_tokens=(meta.get("totalTokens") if isinstance(meta, dict) else None), cost=(meta.get("cost") if isinstance(meta, dict) else None), provider_model=(meta.get("model") if isinstance(meta, dict) else None), response_preview=(preview_text(parsed, RESPONSE_PREVIEW_CHARS) if INCLUDE_RESPONSES else None))
            return parsed
        except Exception as ex:
            elapsed_ms = int((time.time() - started) * 1000)
            last_error = ex
            log_event('llm-request-failed', level='warning', provider=provider, model=model, stage=cfg.stage, attempt=f'{attempt}/{attempts}', elapsed_ms=elapsed_ms, error=str(ex))
            if attempt >= max(1, attempts):
                break
            time.sleep(max(1, backoff) * attempt)
    provider_title = "Ollama" if provider == "ollama" else "LLM provider"
    raise RuntimeError(f"{provider_title} failed after {attempts} attempt(s): {last_error}")


# Historical compatibility export
call_ollama = call_llm
