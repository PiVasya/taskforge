import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore
import prompt_builder  # type: ignore


class ChatMemoryRoutingTests(unittest.TestCase):
    def test_inspect_request_prefers_course_listing_over_bridge_plan(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Изучи задачи курса и выведи их мне сюда"}],
            "memory": {
                "lastBridgePlan": {"summary": "Есть старый план", "items": [{"index": 1}, {"index": 2}]},
                "lastCourseAudit": {"summary": "Есть старый аудит"},
                "agentState": {"placementAfterAssignmentId": "a1"},
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "inspect_course_assignments")
        self.assertNotIn("prepare_bridge_plan", str(result))

    def test_generate_followup_creates_draft_not_bridge_pipeline(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Всё, делай саму задачу"}],
            "memory": {
                "lastBridgePlan": {"summary": "Есть старый план", "items": [{"index": 1}, {"index": 2}]},
                "agentState": {"placementAfterAssignmentId": "a1"},
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "save_chat_blueprint")

    def test_short_followup_without_clear_next_step_asks_question(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "продолжай"}],
            "memory": {},
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"], [])
        self.assertIn("Уточни", result["assistantMessage"])

    def test_prompt_mentions_listing_priority_over_bridges(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {
                "agentState": {"currentStage": "bridge-draft", "userIntentSummary": "Старый план мостиков"},
                "lastBridgePlan": {"summary": "Есть план", "items": [{"index": 1}]},
            },
            "conversation": [{"role": "user", "content": "Покажи существующие задания курса"}],
            "availableActions": [{"name": "inspect_course_assignments"}, {"name": "prepare_bridge_plan"}],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("приоритет у inspect_course_assignments", prompt)
        self.assertIn("Не превращай каждый запрос про курс в bridge-plan workflow", prompt)

    def test_finalize_request_uses_chat_blueprint(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Одобряю, закидывай в черновик"}],
            "memory": {
                "currentDraftBlueprint": {
                    "summary": "Есть варианты",
                    "proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант 1", "conditionPreview": "..."}]
                }
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "finalize_chat_blueprint")

    def test_direct_generate_uses_existing_blueprint(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Не черновик, запускай создание задачи по тому что выше"}],
            "memory": {
                "currentDraftBlueprint": {
                    "approvedForDraft": True,
                    "proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант 1", "assignmentType": "code-test", "difficulty": 1, "conditionPreview": "..."}]
                }
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "queue_generate_from_text")
        self.assertTrue(result["actions"][0]["arguments"].get("useCurrentBlueprint"))

    def test_edit_request_routes_to_revise_draft(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "recentDrafts": [{"id": "d1"}],
            "conversation": [{"role": "user", "content": "Поправь готовый черновик: поменяй формулировку и тесты"}],
            "memory": {},
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "revise_draft_from_chat")
        self.assertEqual(result["actions"][0]["arguments"].get("draftId"), "d1")

    def test_edit_blueprint_request_routes_to_revise_chat_blueprint(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Поправь второй вариант: оставь условие, но замени тесты и шаг 3"}],
            "memory": {
                "currentDraftBlueprint": {
                    "summary": "Есть варианты",
                    "proposals": [
                        {"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант 1", "conditionPreview": "..."},
                        {"id": "22222222-2222-2222-2222-222222222222", "title": "Вариант 2", "conditionPreview": "..."}
                    ]
                }
            },
        }
        result = worker._normalize_chat_turn_result(payload, {
            "assistantMessage": "Обновляю варианты.",
            "draftBlueprint": {
                "summary": "Переписал второй вариант.",
                "proposals": [
                    {"title": "Вариант 1", "conditionPreview": "...", "publicTests": []},
                    {"title": "Вариант 2", "conditionPreview": "Новый текст", "publicTests": [{"input": "1", "expectedOutput": "2"}], "hiddenTests": [{"input": "2", "expectedOutput": "3"}]}
                ]
            }
        })
        self.assertEqual(result["actions"][0]["name"], "revise_chat_blueprint")
        self.assertEqual(result["actions"][0]["arguments"]["proposals"][1]["id"], "22222222-2222-2222-2222-222222222222")


if __name__ == "__main__":
    unittest.main()
