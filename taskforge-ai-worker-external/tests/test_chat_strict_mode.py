import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore


class ChatStrictModeTests(unittest.TestCase):
    def setUp(self):
        self.payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "availableActions": [{"name": "publish_draft"}, {"name": "queue_generate_from_text"}],
            "recentDrafts": [{"id": "d1"}],
            "recentAssignments": [{"id": "a1"}],
            "recentBatches": [{"id": "b1"}],
            "recentUsers": [{"id": "u1"}],
            "recentAttempts": [{"sourceAttemptId": "s1"}],
        }

    def test_unknown_action_removed(self):
        result, issues = worker._apply_chat_strict_mode(self.payload, {"assistantMessage": "ok", "actions": [{"name": "hack_system", "reason": "x", "arguments": {}}]})
        self.assertEqual(result["actions"], [])
        self.assertTrue(any("unknown action" in issue for issue in issues))

    def test_invented_id_removed(self):
        result, issues = worker._apply_chat_strict_mode(self.payload, {"assistantMessage": "ok", "actions": [{"name": "queue_generate_from_text", "reason": "x", "arguments": {"courseId": "c404", "draftId": "d1"}}]})
        self.assertEqual(result["actions"], [])
        self.assertTrue(any("unknown id" in issue for issue in issues))

    def test_destructive_action_requires_confirmed(self):
        result, issues = worker._apply_chat_strict_mode(self.payload, {"assistantMessage": "ok", "actions": [{"name": "publish_draft", "reason": "x", "arguments": {"draftId": "d1"}}]})
        self.assertEqual(result["actions"], [])
        self.assertTrue(any("confirmed" in issue for issue in issues))


if __name__ == "__main__":
    unittest.main()
