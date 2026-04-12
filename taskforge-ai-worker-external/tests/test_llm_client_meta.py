import sys
import unittest
from pathlib import Path
from unittest.mock import patch
ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
import llm_client  # type: ignore
import ollama  # type: ignore
class _Resp:
    def __init__(self, payload, status_code=200):
        self._payload = payload
        self.status_code = status_code
        self.text = str(payload)
    def json(self):
        return self._payload
class TestLlmClientMeta(unittest.TestCase):
    @patch("llm_client.EXTERNAL_AI_API_KEY", "test-key")
    @patch("llm_client.requests.post")
    def test_success_response_contains_hidden_meta(self, post_mock):
        payload = {"choices": [{"message": {"content": '{"assistantMessage":"ok","actions":[]}'}}], "usage": {"prompt_tokens": 11, "completion_tokens": 7, "total_tokens": 18}, "model": "openai/gpt-4.1", "provider": "OpenRouter", "cost": 0.0042}
        post_mock.return_value = _Resp(payload)
        cfg = llm_client.OllamaCallConfig(stage="assistant_chat_turn", json_mode=True)
        result = llm_client.call_llm("Return JSON", cfg)
        assert result["assistantMessage"] == "ok"
        assert result["__llmMeta"]["totalTokens"] == 18
        assert abs(result["__llmMeta"]["cost"] - 0.0042) < 1e-6
    def test_legacy_ollama_module_reexports_new_client(self):
        assert callable(ollama.call_ollama)
        assert callable(ollama.call_llm)
if __name__ == "__main__":
    unittest.main()
