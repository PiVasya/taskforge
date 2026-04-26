from __future__ import annotations

import time
from typing import Any, Dict, Iterable, List, Optional

import requests

from config import (
    EXTERNAL_AI_API_KEY,
    EXTERNAL_AI_BASE_URL,
    EXTERNAL_AI_EXTRA_HEADERS,
    EXTERNAL_AI_MODEL,
    EXTERNAL_AI_REQUEST_ATTEMPTS,
    EXTERNAL_AI_RETRY_BACKOFF_SECONDS,
    EXTERNAL_AI_TEMPERATURE,
    EXTERNAL_AI_TIMEOUT_SECONDS,
    EXTERNAL_AI_TOP_P,
    INCLUDE_PROMPTS,
    INCLUDE_RESPONSES,
    OPENROUTER_APP_TITLE,
    OPENROUTER_APP_URL,
    OPENROUTER_CATEGORIES,
    OPENROUTER_DATA_COLLECTION,
    OPENROUTER_REQUIRE_PARAMETERS,
    OPENROUTER_ZDR,
)
from log import log_event, preview_text


def is_openrouter() -> bool:
    return "openrouter.ai" in (EXTERNAL_AI_BASE_URL or "").lower()


class OpenRouterClient:
    """Minimal OpenRouter/OpenAI-compatible chat client kept after the AI reset."""

    def __init__(self, session: Optional[requests.Session] = None) -> None:
        self.session = session or requests.Session()

    def _headers(self) -> Dict[str, str]:
        if not EXTERNAL_AI_API_KEY:
            raise RuntimeError("TASKFORGE_EXTERNAL_AI_API_KEY is empty")
        headers: Dict[str, str] = {
            "Authorization": f"Bearer {EXTERNAL_AI_API_KEY}",
            "Content-Type": "application/json",
            **EXTERNAL_AI_EXTRA_HEADERS,
        }
        if is_openrouter():
            if OPENROUTER_APP_URL:
                headers.setdefault("HTTP-Referer", OPENROUTER_APP_URL)
            if OPENROUTER_APP_TITLE:
                headers.setdefault("X-Title", OPENROUTER_APP_TITLE)
                headers.setdefault("X-OpenRouter-Title", OPENROUTER_APP_TITLE)
            if OPENROUTER_CATEGORIES:
                headers.setdefault("X-OpenRouter-Categories", OPENROUTER_CATEGORIES)
        return headers

    def chat(
        self,
        messages: Iterable[Dict[str, str]],
        *,
        model: Optional[str] = None,
        temperature: Optional[float] = None,
        top_p: Optional[float] = None,
        response_format: Optional[Dict[str, Any]] = None,
        extra_body: Optional[Dict[str, Any]] = None,
    ) -> Dict[str, Any]:
        body: Dict[str, Any] = {
            "model": model or EXTERNAL_AI_MODEL,
            "messages": list(messages),
            "temperature": EXTERNAL_AI_TEMPERATURE if temperature is None else temperature,
            "top_p": EXTERNAL_AI_TOP_P if top_p is None else top_p,
        }
        if response_format is not None:
            body["response_format"] = response_format
        if is_openrouter():
            body["provider"] = {
                "require_parameters": OPENROUTER_REQUIRE_PARAMETERS,
                "data_collection": OPENROUTER_DATA_COLLECTION,
                "zdr": OPENROUTER_ZDR,
            }
        if extra_body:
            body.update(extra_body)

        if INCLUDE_PROMPTS:
            log_event("openrouter-request", model=body.get("model"), messages=preview_text(str(body.get("messages"))))

        last_error: Exception | None = None
        for attempt in range(1, max(1, EXTERNAL_AI_REQUEST_ATTEMPTS) + 1):
            try:
                response = self.session.post(
                    f"{EXTERNAL_AI_BASE_URL}/chat/completions",
                    headers=self._headers(),
                    json=body,
                    timeout=EXTERNAL_AI_TIMEOUT_SECONDS,
                )
                if response.status_code >= 400:
                    raise RuntimeError(f"HTTP {response.status_code}: {(response.text or '')[:600]}")
                data = response.json()
                if INCLUDE_RESPONSES:
                    log_event("openrouter-response", model=body.get("model"), response=preview_text(str(data)))
                return data
            except Exception as exc:
                last_error = exc
                log_event("openrouter-error", attempt=attempt, error=str(exc))
                if attempt < EXTERNAL_AI_REQUEST_ATTEMPTS:
                    time.sleep(max(0, EXTERNAL_AI_RETRY_BACKOFF_SECONDS))
        raise RuntimeError(f"OpenRouter request failed: {last_error}")

    @staticmethod
    def first_text(data: Dict[str, Any]) -> str:
        choices = data.get("choices") if isinstance(data, dict) else None
        if not choices:
            return ""
        message = choices[0].get("message") if isinstance(choices[0], dict) else None
        if not isinstance(message, dict):
            return ""
        content = message.get("content")
        if isinstance(content, str):
            return content.strip()
        if isinstance(content, list):
            parts: List[str] = []
            for item in content:
                if isinstance(item, dict) and item.get("type") == "text":
                    parts.append(str(item.get("text") or ""))
            return "\n".join(parts).strip()
        return ""
