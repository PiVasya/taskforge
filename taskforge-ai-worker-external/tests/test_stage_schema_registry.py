import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import schemas  # type: ignore
import worker  # type: ignore


class StageSchemaRegistryTests(unittest.TestCase):
    def test_validate_stage_result_reports_chat_errors(self):
        ok, errors = schemas.validate_stage_result("assistant_chat_turn", {"assistantMessage": "ok", "actions": [{"name": "x"}]})
        self.assertFalse(ok)
        self.assertTrue(errors)

    def test_stage_llm_config_attaches_schema(self):
        cfg = worker._stage_llm_config("assistant_chat_turn", {}, 0)
        self.assertIsNotNone(cfg.json_schema)
        self.assertEqual(cfg.json_schema["name"], "assistant_chat_turn")


if __name__ == "__main__":
    unittest.main()
