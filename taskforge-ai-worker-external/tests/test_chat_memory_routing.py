import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore
import prompt_builder  # type: ignore


class ChatMemoryRoutingTests(unittest.TestCase):
    def test_direct_generation_followup_uses_bridge_batch_when_plan_exists(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Всё, делай саму задачу"}],
            "memory": {
                "lastBridgePlan": {"summary": "Есть план", "items": [{"index": 1}, {"index": 2}]},
                "agentState": {"placementAfterAssignmentId": "a1"},
            },
        }
        result = worker._normalize_chat_turn_result(payload, {"assistantMessage": "ok", "actions": []})
        self.assertEqual(result["actions"][0]["name"], "queue_generate_bridge_batch")

    def test_generate_followup_prefers_teaching_script_and_suppresses_plan_loop(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Всё, делай"}],
            "memory": {
                "latestIntentKind": "generate",
                "latestTeachingScript": "#include <iostream>\nint main() {\n  cout << \"Hi\";\n}",
                "suppressBridgePlanLoop": True,
                "lastBridgePlan": {"summary": "Есть план", "items": [{"index": 1}, {"index": 2}]},
                "agentState": {"placementAfterAssignmentId": "a1"},
            },
        }
        result = worker._normalize_chat_turn_result(payload, {"assistantMessage": "ok", "actions": []})
        self.assertEqual(result["actions"][0]["name"], "queue_generate_bridge_batch")
        self.assertIn("#include <iostream>", result["actions"][0]["arguments"].get("prompt", ""))

    def test_revise_followup_infers_second_item(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Сделай задачку, поставь её второй"}],
            "memory": {
                "lastBridgePlan": {"summary": "Есть план", "items": [{"index": 1}, {"index": 2}]},
            },
        }
        result = worker._normalize_chat_turn_result(payload, {"assistantMessage": "ok", "actions": []})
        self.assertEqual(result["actions"][0]["name"], "revise_bridge_plan")
        self.assertEqual(result["actions"][0]["arguments"].get("itemIndex"), 2)

    def test_prompt_mentions_latest_teaching_script_priority(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {
                "agentState": {"currentStage": "bridge-draft", "userIntentSummary": "Сделать мостики"},
                "latestTeachingScript": "#include <iostream>\nint main() { cout << \"Hi\"; }",
            },
            "conversation": [{"role": "user", "content": "Всё, делай саму задачу"}],
            "availableActions": [{"name": "queue_generate_bridge_batch"}, {"name": "revise_bridge_plan"}],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("teaching-script", prompt)
        self.assertIn("приоритет — generation", prompt)


if __name__ == "__main__":
    unittest.main()
