import json
import os
import sys
import unittest
from unittest.mock import patch

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import ollama  # type: ignore
import llm_client  # type: ignore


class _Resp:
    def __init__(self, status_code=200, payload=None, text=""):
        self.status_code = status_code
        self._payload = payload or {}
        self.text = text
        self.reason = ""

    def json(self):
        return self._payload


class OpenRouterAdapterTests(unittest.TestCase):
    def test_openrouter_body_includes_provider_plugins_and_schema(self):
        sent = {}

        def fake_post(url, json=None, headers=None, timeout=None):
            sent["url"] = url
            sent["json"] = json
            sent["headers"] = headers
            return _Resp(payload={"choices": [{"message": {"content": '{"assistantMessage":"ok","actions":[]}'}}]})

        cfg = ollama.OllamaCallConfig(
            stage="assistant_chat_turn",
            json_mode=True,
            json_schema={"name": "assistant_chat_turn", "strict": True, "schema": {"type": "object"}},
        )
        with patch.object(llm_client, "EXTERNAL_AI_API_KEY", "k"), \
             patch.object(llm_client, "EXTERNAL_AI_BASE_URL", "https://openrouter.ai/api/v1"), \
             patch.object(llm_client, "EXTERNAL_AI_MODEL", "qwen/qwen3.6-plus"), \
             patch.object(llm_client, "OPENROUTER_PROVIDER_ORDER", ["anthropic", "openai"]), \
             patch.object(llm_client, "OPENROUTER_PLUGINS", ["response-healing", "context-compression"]), \
             patch.object(llm_client, "OPENROUTER_APP_URL", "https://taskforge.example"), \
             patch.object(llm_client, "OPENROUTER_APP_TITLE", "TaskForge"), \
             patch.object(llm_client.requests, "post", side_effect=fake_post):
            result = ollama.call_ollama("Return JSON", cfg)
        self.assertEqual(result["assistantMessage"], "ok")
        self.assertIn("provider", sent["json"])
        self.assertEqual(sent["json"]["provider"]["order"], ["anthropic", "openai"])
        self.assertTrue(sent["json"]["provider"]["require_parameters"])
        self.assertEqual(sent["json"]["response_format"]["type"], "json_schema")
        self.assertEqual(len(sent["json"]["plugins"]), 2)
        self.assertEqual(sent["headers"]["HTTP-Referer"], "https://taskforge.example")
        self.assertEqual(sent["headers"]["X-OpenRouter-Title"], "TaskForge")


if __name__ == "__main__":
    unittest.main()
