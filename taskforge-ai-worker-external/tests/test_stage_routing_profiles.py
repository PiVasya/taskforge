import os
import sys
import unittest
from unittest.mock import patch

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore


class StageRoutingProfileTests(unittest.TestCase):
    def test_planning_stage_prefers_planning_order(self):
        with patch.object(worker, "STAGE_PROVIDER_ORDER_PLANNING", ["anthropic", "openai"]):
            cfg = worker._stage_llm_config("assignment_batch_plan", {"__compactMode": ""}, 0)  # noqa: SLF001
        self.assertEqual(cfg.routing["order"], ["anthropic", "openai"])
        self.assertTrue(any(p.get("id") == "response-healing" for p in cfg.plugins))

    def test_draft_stage_adds_context_compression_on_retry(self):
        with patch.object(worker, "STAGE_PROVIDER_ORDER_DRAFT", ["openai", "anthropic"]):
            cfg = worker._stage_llm_config("assignment_generate_from_text", {"__compactMode": "compact"}, 1)  # noqa: SLF001
        plugin_ids = [p.get("id") for p in cfg.plugins]
        self.assertEqual(cfg.routing["order"], ["openai", "anthropic"])
        self.assertIn("context-compression", plugin_ids)


if __name__ == "__main__":
    unittest.main()
