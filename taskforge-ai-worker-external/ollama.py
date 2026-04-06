"""External LLM client with provider-aware transport and JSON repair.

Despite the module name and API shape, this file is used by the external AI worker.
It intentionally keeps the `call_ollama` / `OllamaCallConfig` interface so the copied
worker pipeline can remain almost identical to the legacy local-Ollama worker.
"""

import json
import os
import re
import time
from typing import Any, Dict, Iterable

import requests

from config import (
    EXTERNAL_AI_PROVIDER,
    EXTERNAL_AI_BASE_URL,
    EXTERNAL_AI_API_KEY,
    EXTERNAL_AI_ANTHROPIC_VERSION,
    EXTERNAL_AI_EXTRA_HEADERS_RAW,
    EXTERNAL_AI_QWEN_DISABLE_THINKING,
    EXTERNAL_AI_QWEN_ENABLE_SEARCH,
    EXTERNAL_AI_QWEN_THINKING_BUDGET_RAW,
    EXTERNAL_AI_SEED_RAW,
    EXTERNAL_AI_SYSTEM_PROMPT,
    EXTERNAL_AI_TOP_K_RAW,
    EXTERNAL_AI_TOP_P_RAW,
    OLLAMA_MODEL,
    OLLAMA_TEMPERATURE,
    TIMEOUT,
    OLLAMA_REQUEST_ATTEMPTS,
    OLLAMA_RETRY_BACKOFF_SECONDS,
    OLLAMA_JSON_MODE,
)
from log import log




def _parse_optional_int(raw: str) -> int | None:
    raw = str(raw or "").strip()
    if not raw:
        return None
    try:
        return int(raw)
    except Exception:
        return None


def _parse_optional_float(raw: str) -> float | None:
    raw = str(raw or "").strip()
    if not raw:
        return None
    try:
        return float(raw)
    except Exception:
        return None


def _is_qwen_target() -> bool:
    provider = str(EXTERNAL_AI_PROVIDER or "").strip().lower()
    model = str(OLLAMA_MODEL or "").strip().lower()
    base = str(EXTERNAL_AI_BASE_URL or "").strip().lower()
    if provider in {"qwen", "dashscope"}:
        return True
    return "qwen" in model or "dashscope" in base or "aliyuncs.com" in base


def _default_system_prompt(cfg: OllamaCallConfig) -> str:
    if EXTERNAL_AI_SYSTEM_PROMPT:
        return EXTERNAL_AI_SYSTEM_PROMPT
    if cfg.json_mode:
        return (
            "You are TaskForge external AI worker. Follow the task exactly. "
            "Return exactly one valid JSON object and nothing else. "
            "Do not wrap JSON in markdown fences. Do not add explanations before or after the JSON. "
            "Respect the requested schema and keep field names unchanged."
        )
    return "You are TaskForge external AI worker. Follow the task exactly and be concise."


def _openai_messages(prompt: str, cfg: OllamaCallConfig) -> list[dict[str, str]]:
    messages: list[dict[str, str]] = []
    system_prompt = _default_system_prompt(cfg)
    if system_prompt:
        messages.append({"role": "system", "content": system_prompt})
    messages.append({"role": "user", "content": prompt})
    return messages


def _provider_specific_openai_body(cfg: OllamaCallConfig) -> Dict[str, Any]:
    body: Dict[str, Any] = {}
    seed = _parse_optional_int(EXTERNAL_AI_SEED_RAW)
    if seed is not None:
        body["seed"] = seed
    top_p = _parse_optional_float(EXTERNAL_AI_TOP_P_RAW)
    if top_p is not None:
        body["top_p"] = top_p
    top_k = _parse_optional_int(EXTERNAL_AI_TOP_K_RAW)
    if top_k is not None:
        body["top_k"] = top_k
    if _is_qwen_target():
        if EXTERNAL_AI_QWEN_DISABLE_THINKING:
            body["enable_thinking"] = False
        if not EXTERNAL_AI_QWEN_ENABLE_SEARCH:
            body["enable_search"] = False
        thinking_budget = _parse_optional_int(EXTERNAL_AI_QWEN_THINKING_BUDGET_RAW)
        if thinking_budget is not None:
            body["thinking_budget"] = thinking_budget
    return body

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
        raise RuntimeError(f"Could not parse external LLM JSON response: {last_error}")
    parsed_candidates.sort(key=lambda item: item[0], reverse=True)
    best_score, best_label, best_parsed, best_variant = parsed_candidates[0]
    if best_label != "raw":
        log(f"external-llm json repaired via {best_label} response_len={len(raw)} candidate_len={len(best_variant)} score={best_score}")
    return best_parsed


def _extra_headers() -> Dict[str, str]:
    raw = (EXTERNAL_AI_EXTRA_HEADERS_RAW or "").strip()
    if not raw:
        return {}
    try:
        parsed = json.loads(raw)
        if isinstance(parsed, dict):
            return {str(k): str(v) for k, v in parsed.items()}
    except Exception:
        pass
    headers: Dict[str, str] = {}
    for line in raw.splitlines():
        if ":" not in line:
            continue
        name, value = line.split(":", 1)
        headers[name.strip()] = value.strip()
    return headers


def _extract_message_text(data: Dict[str, Any]) -> str:
    if EXTERNAL_AI_PROVIDER == "anthropic":
        content = data.get("content") or []
        parts: list[str] = []
        for item in content:
            if isinstance(item, dict):
                if item.get("type") == "text":
                    parts.append(str(item.get("text") or ""))
            elif isinstance(item, str):
                parts.append(item)
        return "\n".join(x for x in parts if x).strip()

    choices = data.get("choices") or []
    if not choices:
        return ""
    message = choices[0].get("message") if isinstance(choices[0], dict) else {}
    content = message.get("content") if isinstance(message, dict) else ""
    if isinstance(content, list):
        parts: list[str] = []
        for item in content:
            if isinstance(item, dict):
                if item.get("type") in {"text", "output_text"}:
                    parts.append(str(item.get("text") or item.get("content") or ""))
                elif "text" in item:
                    parts.append(str(item.get("text") or ""))
            elif isinstance(item, str):
                parts.append(item)
        return "\n".join(x for x in parts if x).strip()
    return str(content or "").strip()


def _request_openai_compatible(prompt: str, cfg: OllamaCallConfig, timeout: int, use_json_hint: bool) -> Dict[str, Any]:
    headers = {
        "Authorization": f"Bearer {EXTERNAL_AI_API_KEY}",
        "Content-Type": "application/json",
    }
    headers.update(_extra_headers())
    body: Dict[str, Any] = {
        "model": OLLAMA_MODEL,
        "messages": _openai_messages(prompt, cfg),
        "temperature": cfg.temperature,
    }
    body.update(_provider_specific_openai_body(cfg))
    if cfg.num_predict is not None:
        body["max_tokens"] = cfg.num_predict
    if use_json_hint and cfg.json_mode:
        body["response_format"] = {"type": "json_object"}
    resp = requests.post(
        f"{EXTERNAL_AI_BASE_URL}/chat/completions",
        json=body,
        headers=headers,
        timeout=timeout,
    )
    if resp.status_code >= 400:
        raise RuntimeError(f"HTTP {resp.status_code}: {resp.text[:500]}")
    return resp.json()


def _request_anthropic(prompt: str, cfg: OllamaCallConfig, timeout: int) -> Dict[str, Any]:
    headers = {
        "x-api-key": EXTERNAL_AI_API_KEY,
        "anthropic-version": EXTERNAL_AI_ANTHROPIC_VERSION,
        "content-type": "application/json",
    }
    headers.update(_extra_headers())
    body: Dict[str, Any] = {
        "model": OLLAMA_MODEL,
        "max_tokens": cfg.num_predict or 1024,
        "temperature": cfg.temperature,
        "messages": _openai_messages(prompt, cfg),
    }
    resp = requests.post(
        f"{EXTERNAL_AI_BASE_URL}/messages",
        json=body,
        headers=headers,
        timeout=timeout,
    )
    if resp.status_code >= 400:
        raise RuntimeError(f"HTTP {resp.status_code}: {resp.text[:500]}")
    return resp.json()


def call_ollama(prompt: str, config: OllamaCallConfig | None = None) -> Dict[str, Any]:
    cfg = config or OllamaCallConfig()
    provider = EXTERNAL_AI_PROVIDER or "openai_compatible"
    extras = _provider_specific_openai_body(cfg) if provider != 'anthropic' else {}
    log(
        f"external-llm >>> provider={provider} model={OLLAMA_MODEL} stage={cfg.stage} prompt_len={len(prompt)} "
        f"attempts={cfg.attempts} timeout={cfg.timeout}s format={'json' if cfg.json_mode else 'text'} "
        f"num_predict={cfg.num_predict or '-'} extras={json.dumps(extras, ensure_ascii=False) if extras else '-'}"
    )
    last_error: Exception | None = None
    for attempt in range(1, max(1, cfg.attempts) + 1):
        started = time.time()
        try:
            if not EXTERNAL_AI_API_KEY:
                raise RuntimeError("TASKFORGE_EXTERNAL_AI_API_KEY is empty")
            if provider == "anthropic":
                data = _request_anthropic(prompt, cfg, cfg.timeout)
            elif provider in {"openai", "openai_compatible", "deepseek", "openrouter", "dashscope", "qwen"}:
                try:
                    data = _request_openai_compatible(prompt, cfg, cfg.timeout, True)
                except Exception as ex:
                    # Some providers reject response_format=json_object. Retry once without it.
                    if cfg.json_mode and attempt <= cfg.attempts:
                        log(f"external-llm json_hint rejected for stage={cfg.stage}; retrying without response_format: {ex}")
                        data = _request_openai_compatible(prompt, cfg, cfg.timeout, False)
                    else:
                        raise
            else:
                raise RuntimeError(f"Unsupported external provider: {provider}")
            elapsed_ms = int((time.time() - started) * 1000)
            raw = _extract_message_text(data)
            if not raw:
                raise RuntimeError("External LLM returned empty response")
            parsed = _parse_json_response(raw, required_keys=cfg.required_keys, preferred_keys=cfg.preferred_keys) if cfg.json_mode else {"text": raw}
            log(f"external-llm <<< provider={provider} stage={cfg.stage} attempt={attempt}/{cfg.attempts} {elapsed_ms}ms response_len={len(raw)}")
            return parsed
        except Exception as ex:
            elapsed_ms = int((time.time() - started) * 1000)
            last_error = ex
            log(f"external-llm !!! provider={provider} stage={cfg.stage} attempt={attempt}/{cfg.attempts} failed after {elapsed_ms}ms error={ex}")
            if attempt >= max(1, cfg.attempts):
                break
            time.sleep(max(1, OLLAMA_RETRY_BACKOFF_SECONDS) * attempt)
    raise RuntimeError(f"External LLM failed after {cfg.attempts} attempt(s): {last_error}")
