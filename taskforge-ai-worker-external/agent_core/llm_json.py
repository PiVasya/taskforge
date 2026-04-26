from __future__ import annotations

import json
import re
from dataclasses import dataclass
from typing import Any, Dict, Iterable, List, Optional

from config import EXTERNAL_AI_API_KEY
from log import log_event, preview_text
from openrouter_client import OpenRouterClient


class LlmJsonError(RuntimeError):
    pass


@dataclass
class LlmJsonResult:
    data: Dict[str, Any]
    raw_text: str
    model_used: bool


def compact_json(value: Any, max_chars: int = 18000) -> str:
    text = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    if len(text) <= max_chars:
        return text
    return text[:max_chars] + "...TRUNCATED"


def extract_json_object(text: str) -> Dict[str, Any]:
    raw = (text or "").strip()
    if not raw:
        raise LlmJsonError("LLM returned empty text")

    if raw.startswith("```"):
        raw = re.sub(r"^```(?:json)?\s*", "", raw, flags=re.IGNORECASE).strip()
        raw = re.sub(r"\s*```$", "", raw).strip()

    try:
        parsed = json.loads(raw)
        if isinstance(parsed, dict):
            return parsed
    except Exception:
        pass

    start = raw.find("{")
    end = raw.rfind("}")
    if start >= 0 and end > start:
        candidate = raw[start : end + 1]
        try:
            parsed = json.loads(candidate)
            if isinstance(parsed, dict):
                return parsed
        except Exception as exc:
            raise LlmJsonError(f"Cannot parse JSON object from LLM text: {exc}") from exc

    raise LlmJsonError("LLM did not return a JSON object")


class LlmJsonClient:
    def __init__(self, client: Optional[OpenRouterClient] = None) -> None:
        self.client = client or OpenRouterClient()

    def generate(self, *, system: str, user: str, purpose: str, temperature: Optional[float] = None) -> LlmJsonResult:
        if not EXTERNAL_AI_API_KEY:
            raise LlmJsonError("TASKFORGE_EXTERNAL_AI_API_KEY is empty; real LLM generation is unavailable")

        messages: List[Dict[str, str]] = [
            {"role": "system", "content": system.strip()},
            {"role": "user", "content": user.strip()},
        ]
        log_event("llm-json-request", purpose=purpose, user_preview=preview_text(user, 900))
        data = self.client.chat(
            messages,
            temperature=temperature,
            response_format={"type": "json_object"},
        )
        text = self.client.first_text(data)
        parsed = extract_json_object(text)
        log_event("llm-json-response", purpose=purpose, chars=len(text), keys=list(parsed.keys())[:20])
        return LlmJsonResult(data=parsed, raw_text=text, model_used=True)
