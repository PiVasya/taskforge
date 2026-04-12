import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore


class ChatStageConfigTests(unittest.TestCase):
    def test_chat_stage_uses_json_mode_without_json_schema(self):
        cfg = worker._stage_llm_config("assistant_chat_turn", {}, 0)
        self.assertTrue(cfg.json_mode)
        self.assertIsNone(cfg.json_schema)


if __name__ == "__main__":
    unittest.main()
