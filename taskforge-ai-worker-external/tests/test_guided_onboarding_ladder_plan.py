import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore
import worker  # type: ignore
from scenario_router import detect_scenario_profile  # type: ignore


class GuidedOnboardingLadderPlanTests(unittest.TestCase):
    def test_onboarding_ladder_markers_select_guided_scenario_without_exact_style_words(self):
        text = "Нужны 5 обучалок лесенкой перед if, мягко подвести и научить пользоваться"
        profile = detect_scenario_profile({"prompt": text, "sourceText": text}, requested_count=5)
        self.assertEqual(profile.get("id"), "guided-onboarding-ladder")
        self.assertTrue(profile.get("prefer_guided"))

    def test_requested_count_five_is_repaired_before_save(self):
        user = "Сделай 5 обучалок лесенкой перед if"
        data = {
            "conversation": [{"role": "user", "content": user}],
            "memory": {"latestExplicitInstruction": user},
        }
        result = {
            "assistantMessage": "Собрала черновики.",
            "draftBlueprint": {
                "proposals": [
                    {"title": "Простой if", "conditionPreview": "Напишите программу с простым if."},
                    {"title": "if else", "conditionPreview": "Напишите программу с if else."},
                    {"title": "else if", "conditionPreview": "Напишите программу с else if."},
                ]
            },
            "count": 5,
        }
        proposals = worker._chat_build_blueprint_proposals(data, result, user, user, 5, "code-test", 2)
        self.assertEqual(len(proposals), 5)
        self.assertTrue(all("Следуй шагам" in proposal.get("fullCondition", "") for proposal in proposals))
        self.assertTrue(all(proposal.get("title", "").startswith(f"Задание {idx}.") for idx, proposal in enumerate(proposals, start=1)))

    def test_needs_revision_count_or_style_auto_revises_without_validator_leak(self):
        user = "Сделай 5 задач лесенкой перед if"
        existing = [
            {"title": "Простой if", "conditionPreview": "Напишите программу с if."},
            {"title": "if else", "conditionPreview": "Напишите программу с if else."},
            {"title": "else if", "conditionPreview": "Напишите программу с else if."},
        ]
        data = {
            "courseId": "c1",
            "conversation": [
                {"role": "user", "content": user},
                {
                    "role": "assistant",
                    "content": "Blueprint пока не удовлетворяет явной инструкции пользователя.",
                    "toolResults": [
                        {
                            "status": "needs-revision",
                            "summary": "Blueprint пока не удовлетворяет явной инструкции пользователя. Пользователь просил 5 задач(и), а в blueprint сейчас 3.",
                        }
                    ],
                },
            ],
            "memory": {"latestExplicitInstruction": user, "currentDraftBlueprint": {"proposals": existing}},
        }
        result = {
            "assistantMessage": "Blueprint пока не удовлетворяет явной инструкции пользователя.",
            "draftBlueprint": {"summary": "Исправленная лесенка", "proposals": existing},
            "count": 5,
        }
        normalized = worker._normalize_chat_turn_result(data, result)
        self.assertEqual(normalized["actions"][0]["name"], "revise_chat_blueprint")
        self.assertNotIn("Blueprint пока", normalized["assistantMessage"])
        self.assertNotIn("needs-revision", normalized["assistantMessage"].lower())
        self.assertEqual(len(normalized["actions"][0]["arguments"]["proposals"]), 5)

    def test_onboarding_request_auto_selects_guided_intro_exemplar_after_inspection(self):
        data = {
            "assignmentType": "code-test",
            "prompt": "Сделай 5 обучалок лесенкой перед if",
            "sourceText": "Нужно мягко подвести к if без явной просьбы копировать первое задание.",
            "referenceAssignments": [
                {
                    "id": "a1",
                    "title": "Задание 1. Твой первый вывод",
                    "description": "Давай напишем первую программу.\n\nСледуй шагам:\n1. Напиши cout.\n\nЗапусти код и посмотри результат.",
                    "sort": 0,
                    "aiOverview": {"pedagogicalRole": "guided-intro", "teachingStyle": "friendly walkthrough", "isImportant": True},
                },
                {"id": "a7", "title": "Задание 7. Сухая проверка", "description": "Даны числа. Найдите ответ.", "sort": 7},
            ],
        }
        compact = payload.compact_payload_for_stage("draft_body_generate", data)
        anchor_context = compact.get("anchorContext") or {}
        exemplars = anchor_context.get("styleExemplarAssignments") or []
        self.assertTrue(any(item.get("id") == "a1" for item in exemplars))
        self.assertFalse(anchor_context.get("exactStyleRequested"))

    def test_guided_ladder_sets_style_contract_without_exact_style_words(self):
        user = "Сделай обучалку лесенкой перед if"
        data = {"assignmentType": "code-test", "prompt": user, "sourceText": user}
        result = {
            "draft": {
                "assignmentType": "code-test",
                "title": "Проверка числа",
                "description": "Напишите программу с if.",
                "publicTests": [{"input": "1", "expectedOutput": "ok\n"}],
                "hiddenTests": [],
                "referenceSolutionPython": "print('ok')",
            }
        }
        normalized = payload._synthesize_generation_result(data, result)
        contract = normalized["draft"]["meta"]["styleContract"]
        self.assertTrue(contract["preferGuidedIntroScaffold"])
        self.assertTrue(contract["avoidGenericCommentary"])
        self.assertEqual(contract["scenarioId"], "guided-onboarding-ladder")


if __name__ == "__main__":
    unittest.main()
