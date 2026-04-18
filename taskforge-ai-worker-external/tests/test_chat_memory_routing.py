import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore
import prompt_builder  # type: ignore


class ChatMemoryRoutingTests(unittest.TestCase):
    def test_gap_remediation_request_prefers_advance_agent_stage(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Сгенерируй задачи на все пробелы в курсе и предложи решения"}],
            "memory": {
                "agentState": {
                    "objectiveKind": "course-gap-remediation",
                    "objectiveSummary": "Найти пробелы, проверить соседние задания и собрать решения",
                    "nextSuggestedAction": "запустить discovery по курсу через analyze_course_progression",
                }
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "advance_agent_stage")
        self.assertIn("полный проход", result["assistantMessage"].lower())


    def test_precision_audit_request_uses_inspection_when_audit_already_exists(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Точно ли нету обучалки cout в курсе? посмотри точнее"}],
            "memory": {
                "lastCourseAudit": {"summary": "Есть старый аудит"},
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "inspect_course_assignments")

    def test_precision_audit_request_can_chain_audit_and_inspection(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Проверь точнее по реальным условиям, где именно в курсе новые функции без обучалки"}],
            "memory": {},
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(len(result["actions"]), 2)
        self.assertEqual(result["actions"][0]["name"], "analyze_course_progression")
        self.assertEqual(result["actions"][1]["name"], "inspect_course_assignments")

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

    def test_strong_style_feedback_with_blueprint_routes_to_revise_blueprint(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Сделай 1 в 1 как первое задание, пошагово"}],
            "memory": {
                "currentDraftBlueprint": {
                    "summary": "Есть варианты",
                    "proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант 1", "conditionPreview": "..."}]
                }
            },
        }
        intent = worker._chat_latest_intent_kind(payload, payload["conversation"][0]["content"], "")
        self.assertEqual(intent, "revise-blueprint")

    def test_autonomous_rework_with_blueprint_routes_to_revise_blueprint(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Повтори решение заново, не останавливайся на промежуточном ответе и если найдёшь проблему — сразу исправь результат полностью"}],
            "memory": {
                "currentDraftBlueprint": {
                    "summary": "Есть варианты",
                    "proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант 1", "conditionPreview": "..."}]
                }
            },
        }
        intent = worker._chat_latest_intent_kind(payload, payload["conversation"][0]["content"], "")
        self.assertEqual(intent, "revise-blueprint")

    def test_prompt_autonomy_mentions_stale_blueprint_must_not_dominate(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {
                "preferAutonomousCompletion": True,
                "currentDraftBlueprint": {"summary": "Есть варианты", "proposals": [{"id": "11111111-1111-1111-1111-111111111111", "title": "Вариант 1", "conditionPreview": "..."}]},
            },
            "conversation": [{"role": "user", "content": "Повтори решение заново и исправь полностью"}],
            "availableActions": [{"name": "inspect_course_assignments"}, {"name": "revise_chat_blueprint"}, {"name": "finalize_chat_blueprint"}],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("не считай старый blueprint священным", prompt)
        self.assertIn("исправь или пересобери blueprint", prompt)

    def test_short_followup_uses_plan_steps_to_continue(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "дальше"}],
            "memory": {
                "agentState": {
                    "confidencePercent": 72,
                    "autonomyMode": "guided-proactive",
                    "planSteps": [
                        {"key": "verify", "title": "Проверить соседние задания", "status": "current", "recommendedAction": "inspect_course_assignments", "summary": "Подтвердить спорные места по реальным заданиям."}
                    ],
                    "decisionCandidates": [{"name": "inspect_course_assignments", "why": "Нужно проверить соседние задания", "status": "preferred"}],
                }
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"][0]["name"], "advance_agent_stage")

    def test_short_followup_with_blocker_summary_stops_for_clarification(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "продолжай"}],
            "memory": {
                "agentState": {
                    "confidencePercent": 48,
                    "needsClarification": True,
                    "autonomyMode": "ask-first",
                    "blockerSummary": "Нужно сначала понять, какие именно пробелы закрывать: весь курс или только ввод/вывод.",
                    "openQuestions": ["Уточнить фокус remediation."],
                }
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"], [])
        self.assertIn("лучше не прыгать", result["assistantMessage"])

    def test_short_followup_with_low_confidence_surfaces_blocker(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "дальше"}],
            "memory": {
                "agentState": {
                    "confidencePercent": 20,
                    "openQuestions": ["Нужно сначала подтвердить пробелы по курсу."],
                    "decisionCandidates": [{"name": "analyze_course_progression", "why": "Сначала нужен discovery", "status": "preferred"}],
                }
            },
        }
        result = worker._normalize_chat_turn_result(payload, {})
        self.assertEqual(result["actions"], [])
        self.assertIn("не хватает опоры", result["assistantMessage"])

    def test_reset_from_scratch_is_not_misclassified_as_plan(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Теперь не продолжай старый план и повтори решение заново с нуля. Все предыдущие черновики недействительны."}],
            "memory": {},
        }
        intent = worker._chat_latest_intent_kind(payload, payload["conversation"][0]["content"], "")
        self.assertEqual(intent, "generate")

    def test_autonomous_generate_fallback_does_not_ask_for_approval(self):
        payload = {
            "courseId": "c1",
            "selectedCourse": {"id": "c1"},
            "conversation": [{"role": "user", "content": "Сделай всё за одно сообщение, не проси одобрение и сгенерируй 2 задачи сам."}],
            "memory": {"preferAutonomousCompletion": True},
        }
        result = worker._normalize_chat_turn_result(payload, {"assistantMessage": "", "count": 2})
        self.assertEqual(result["actions"][0]["name"], "save_chat_blueprint")
        self.assertNotIn("одоб", result["assistantMessage"].lower())


if __name__ == "__main__":
    unittest.main()
