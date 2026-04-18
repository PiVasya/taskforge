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
            "availableActions": [{"name": "publish_draft"}, {"name": "queue_generate_from_text"}, {"name": "save_chat_blueprint"}, {"name": "revise_chat_blueprint"}, {"name": "finalize_chat_blueprint"}, {"name": "revise_draft_from_chat"}],
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

    def test_direct_generation_requires_blueprint_approval(self):
        payload = dict(self.payload)
        payload["conversation"] = [{"role": "user", "content": "Сделай задачу"}]
        result, issues = worker._apply_chat_strict_mode(payload, {"assistantMessage": "ok", "actions": [{"name": "queue_generate_from_text", "reason": "x", "arguments": {"courseId": "c1"}}]})
        self.assertEqual(result["actions"], [])
        self.assertTrue(any("blueprint approval" in issue for issue in issues))

    def test_explicit_generate_phrase_allows_generation_with_blueprint(self):
        payload = dict(self.payload)
        payload["conversation"] = [{"role": "user", "content": "Не черновик, всё генерируй и я её опубликую"}]
        payload["memory"] = {"currentDraftBlueprint": {"approvedForDraft": True, "proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант"}]}}
        result, issues = worker._apply_chat_strict_mode(payload, {"assistantMessage": "ok", "actions": [{"name": "queue_generate_from_text", "reason": "x", "arguments": {"courseId": "c1"}}]})
        self.assertEqual(len(result["actions"]), 1)
        self.assertEqual(issues, [])

    def test_finalize_blueprint_autofills_course_id(self):
        payload = dict(self.payload)
        payload["conversation"] = [{"role": "user", "content": "одобряю, закидывай в черновик"}]
        payload["memory"] = {"currentDraftBlueprint": {"proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант"}]}}
        result, issues = worker._apply_chat_strict_mode(payload, {"assistantMessage": "ok", "actions": [{"name": "finalize_chat_blueprint", "reason": "x", "arguments": {}}]})
        self.assertEqual(issues, [])
        self.assertEqual(result["actions"][0]["arguments"]["courseId"], "c1")

    def test_prepare_bridge_plan_without_audit_is_blocked(self):
        payload = dict(self.payload)
        payload["availableActions"] = payload["availableActions"] + [{"name": "prepare_bridge_plan"}]
        payload["conversation"] = [{"role": "user", "content": "собери план мостиков"}]
        result, issues = worker._apply_chat_strict_mode(payload, {"assistantMessage": "ok", "actions": [{"name": "prepare_bridge_plan", "reason": "x", "arguments": {}}]})
        self.assertEqual(result["actions"], [])
        self.assertTrue(any("course audit" in issue for issue in issues))

    def test_finalize_blueprint_allowed_after_same_turn_save_in_autonomy(self):
        payload = dict(self.payload)
        payload["conversation"] = [{"role": "user", "content": "Сделай всё за одно сообщение, без промежуточного согласования"}]
        payload["memory"] = {"preferAutonomousCompletion": True}
        result, issues = worker._apply_chat_strict_mode(payload, {
            "assistantMessage": "ok",
            "actions": [
                {"name": "save_chat_blueprint", "reason": "x", "arguments": {"courseId": "c1", "summary": "s", "proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант", "conditionPreview": "test"}]}},
                {"name": "finalize_chat_blueprint", "reason": "x", "arguments": {"courseId": "c1"}},
            ],
        })
        self.assertEqual([x["name"] for x in result["actions"]], ["save_chat_blueprint", "finalize_chat_blueprint"])
        self.assertEqual(issues, [])

    def test_pascal_case_memory_fields_enable_autonomous_finalize(self):
        payload = dict(self.payload)
        payload["conversation"] = [{"role": "user", "content": "Покажи только итог и не проси одобрение"}]
        payload["memory"] = {
            "PreferAutonomousCompletion": True,
            "CurrentDraftBlueprint": {
                "ApprovedForDraft": False,
                "Proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант"}],
            },
        }
        result, issues = worker._apply_chat_strict_mode(payload, {"assistantMessage": "ok", "actions": [{"name": "finalize_chat_blueprint", "reason": "x", "arguments": {}}]})
        self.assertEqual(len(result["actions"]), 1)
        self.assertEqual(result["actions"][0]["arguments"]["courseId"], "c1")
        self.assertEqual(issues, [])


if __name__ == "__main__":
    unittest.main()
