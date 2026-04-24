import os
import sys
from unittest.mock import patch
import unittest

os.environ.setdefault("TASKFORGE_DISABLE_LLM_SLOT_EXPANSION", "1")
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


class GuidedOnboardingRealExecutionPathTests(unittest.TestCase):
    def test_direct_save_action_is_repaired_before_tool_call(self):
        user = "Мне нужны задачи обучалки к if\n\nсделай набор черновиков обучалок перед if"
        data = {
            "courseId": "c1",
            "conversation": [{"role": "user", "content": user}],
            "memory": {
                "latestIntentKind": "generate",
                "latestExplicitInstruction": user,
                "summary": "Дирижёр следит: Количество новых задач должно быть ровно 5. Нужна серия маленьких программ, которые пошагово учат самому использованию if.",
            },
        }
        result = {
            "assistantMessage": "Сохраняю условия",
            "actions": [{
                "name": "save_chat_blueprint",
                "reason": "raw llm action",
                "arguments": {
                    "courseId": "c1",
                    "summary": "Три обучалки по if",
                    "proposals": [
                        {"title": "Обучалка 1", "fullCondition": "Напишите программу с if."},
                        {"title": "Обучалка 2", "fullCondition": "Напишите программу с if else."},
                        {"title": "Обучалка 3", "fullCondition": "Напишите программу с диапазоном."},
                    ],
                },
            }],
        }
        normalized = worker._normalize_chat_turn_result(data, result)
        self.assertEqual(normalized["actions"][0]["name"], "save_chat_blueprint")
        proposals = normalized["actions"][0]["arguments"]["proposals"]
        self.assertEqual(len(proposals), 5)
        self.assertTrue(all("Следуй шагам" in proposal.get("fullCondition", "") for proposal in proposals))

    def test_count_repair_does_not_turn_meta_assistant_text_into_missing_tasks(self):
        user = "Мне нужны задачи обучалки к if\n\nсделай набор черновиков обучалок перед if"
        meta_message = (
            "Я подготовил три варианта подготовительных задач, которые плавно подводят к теме if. "
            "Они отрабатывают сравнения, логические выражения и работу с остатком от деления. "
            "Сохранил их как черновики. Посмотри условия: нужно что-то упростить, усложнить или сразу одобряю для генерации?"
        )
        data = {
            "courseId": "c1",
            "conversation": [{"role": "user", "content": user}],
            "memory": {
                "latestIntentKind": "generate",
                "latestExplicitInstruction": user,
                "summary": "Количество новых задач должно быть ровно 5. Нужна серия маленьких программ, которые пошагово учат самому использованию if.",
            },
        }
        result = {
            "assistantMessage": meta_message,
            "actions": [{
                "name": "save_chat_blueprint",
                "arguments": {
                    "courseId": "c1",
                    "summary": "Три обучалки по if",
                    "proposals": [
                        {"title": "Обучалка 1", "fullCondition": "Считай два числа и выведи результаты сравнений."},
                        {"title": "Обучалка 2", "fullCondition": "Считай число x и проверь диапазон от 10 до 50."},
                        {"title": "Обучалка 3", "fullCondition": "Считай число n и проверь остаток от деления на 2."},
                    ],
                },
            }],
        }
        normalized = worker._normalize_chat_turn_result(data, result)
        proposals = normalized["actions"][0]["arguments"]["proposals"]
        self.assertEqual(len(proposals), 5)
        forbidden = ["я подготовил", "сохранил", "посмотри условия", "сразу одобряю", "варианта подготовительных задач"]
        for proposal in proposals[3:]:
            text = (proposal.get("conditionPreview", "") + "\n" + proposal.get("fullCondition", "")).lower()
            self.assertFalse(any(marker in text for marker in forbidden), text)
            self.assertIn("Следуй шагам", proposal.get("fullCondition", ""))
            self.assertRegex(proposal.get("fullCondition", "").lower(), r"if|условн|провер")

    def test_existing_meta_filler_slots_are_replaced_during_ladder_repair(self):
        user = "Сделай 5 обучалок лесенкой перед if"
        meta_condition = (
            "Что нужно сделать: Я подготовил три варианта подготовительных задач, которые плавно подводят к теме if. "
            "Сохранил их как черновики. Посмотри условия: нужно что-то упростить, усложнить или сразу одобряю для генерации?"
        )
        data = {
            "courseId": "c1",
            "conversation": [{"role": "user", "content": user}],
            "memory": {"latestExplicitInstruction": user},
        }
        result = {
            "assistantMessage": "Я собрала лесенку в нужной форме.",
            "draftBlueprint": {
                "proposals": [
                    {"title": "Задание 1", "fullCondition": "Считай два числа и выведи результаты сравнений."},
                    {"title": "Задание 2", "fullCondition": "Считай число x и проверь диапазон от 10 до 50."},
                    {"title": "Задание 3", "fullCondition": "Считай число n и проверь остаток от деления на 2."},
                    {"title": "Задание 4. Маленький шаг 4", "fullCondition": meta_condition},
                    {"title": "Задание 5. Маленький шаг 5", "fullCondition": meta_condition},
                ]
            },
            "count": 5,
        }
        proposals = worker._chat_build_blueprint_proposals(data, result, user, user, 5, "code-test", 2)
        self.assertEqual(len(proposals), 5)
        for proposal in proposals[3:]:
            text = (proposal.get("conditionPreview", "") + "\n" + proposal.get("fullCondition", "")).lower()
            self.assertNotIn("я подготовил", text)
            self.assertNotIn("сохранил", text)
            self.assertNotIn("посмотри условия", text)
            self.assertIn("Следуй шагам", proposal.get("fullCondition", ""))
            self.assertRegex(proposal.get("fullCondition", "").lower(), r"if|условн|провер")

    def test_failed_first_save_needs_revision_repairs_without_current_blueprint(self):
        user = "Мне нужны задачи обучалки к if\n\nсделай набор черновиков обучалок перед if"
        raw = [
            {"title": "Обучалка 1", "fullCondition": "Напишите программу с if."},
            {"title": "Обучалка 2", "fullCondition": "Напишите программу с if else."},
            {"title": "Обучалка 3", "fullCondition": "Напишите программу с диапазоном."},
        ]
        data = {
            "courseId": "c1",
            "conversation": [
                {"role": "user", "content": user},
                {"role": "assistant", "content": "Blueprint пока не удовлетворяет явной инструкции пользователя.", "toolResults": [{"status": "needs-revision", "summary": "Blueprint пока не удовлетворяет явной инструкции пользователя. Пользователь просил 5 задач(и), а в blueprint сейчас 3. Нужен дружелюбный пошаговый scaffold."}]},
            ],
            "memory": {
                "latestIntentKind": "generate",
                "latestExplicitInstruction": user,
                "currentDraftBlueprint": None,
            },
        }
        result = {
            "assistantMessage": "Остановила авто-исправление, потому что одно и то же замечание повторяется.",
            "actions": [{"name": "save_chat_blueprint", "arguments": {"courseId": "c1", "summary": "raw", "proposals": raw}}],
        }
        normalized = worker._normalize_chat_turn_result(data, result)
        self.assertEqual(normalized["actions"][0]["name"], "revise_chat_blueprint")
        self.assertNotIn("Остановила", normalized["assistantMessage"])
        self.assertNotIn("needs-revision", normalized["assistantMessage"].lower())
        self.assertEqual(len(normalized["actions"][0]["arguments"]["proposals"]), 5)



class GuidedOnboardingSlotExpansionTests(unittest.TestCase):
    def test_missing_slots_are_generated_one_by_one_before_deterministic_count_fallback(self):
        user = "Сделай 5 обучалок лесенкой перед if"
        calls = []

        def fake_call(prompt_text, cfg):
            calls.append(prompt_text)
            slot = 3 + len(calls)
            return {
                "title": f"LLM слот {slot}",
                "conditionPreview": f"Давай сделаем отдельную маленькую программу для слота {slot} по if.",
                "fullCondition": (
                    f"Давай сделаем отдельную маленькую программу для слота {slot}. Она будет тренировать if живым шагом.\n\n"
                    "Следуй шагам:\n"
                    "1. Считай целое число x.\n"
                    "(Так программа получит число для проверки.)\n"
                    "2. Используй if и выбери подходящий вывод.\n"
                    "(Так ученик видит, где программа принимает решение.)\n"
                    "3. Выведи результат на экран.\n"
                    "(После запуска сразу видно, сработала ли проверка.)\n\n"
                    "Запусти код и посмотри, какой результат появится на экране."
                ),
                "goal": f"Сгенерированный отдельным LLM-запросом слот {slot}.",
            }

        data = {
            "courseId": "c1",
            "conversation": [{"role": "user", "content": user}],
            "memory": {
                "latestIntentKind": "generate",
                "latestExplicitInstruction": user,
                "summary": "Нужно ровно 5 дружелюбных обучалок перед if.",
            },
        }
        result = {
            "assistantMessage": "Сохраняю условия",
            "actions": [{
                "name": "save_chat_blueprint",
                "reason": "raw llm action",
                "arguments": {
                    "courseId": "c1",
                    "summary": "Три обучалки по if",
                    "proposals": [
                        {"title": "Обучалка 1", "fullCondition": "Напишите программу с if."},
                        {"title": "Обучалка 2", "fullCondition": "Напишите программу с if else."},
                        {"title": "Обучалка 3", "fullCondition": "Напишите программу с диапазоном."},
                    ],
                },
            }],
        }

        with patch.dict(os.environ, {"TASKFORGE_DISABLE_LLM_SLOT_EXPANSION": "0"}):
            with patch("worker.call_llm", side_effect=fake_call):
                normalized = worker._normalize_chat_turn_result(data, result)

        proposals = normalized["actions"][0]["arguments"]["proposals"]
        self.assertEqual(len(proposals), 5)
        self.assertEqual(len(calls), 2)
        self.assertIn("Номер слота: 4 из 5", calls[0])
        self.assertIn("Номер слота: 5 из 5", calls[1])
        self.assertIn("LLM слот 4", proposals[3].get("title", ""))
        self.assertIn("LLM слот 5", proposals[4].get("title", ""))
        reason = normalized["actions"][0].get("reason", "").lower()
        self.assertNotIn("count repair", reason)
        self.assertNotIn("count/style", reason)

    def test_meta_llm_slot_response_falls_back_to_safe_real_task(self):
        user = "Сделай 5 обучалок лесенкой перед if"

        def fake_meta_call(prompt_text, cfg):
            return {
                "title": "Я подготовил варианты",
                "conditionPreview": "Я подготовил три варианта и сохранил их как черновики.",
                "fullCondition": "Я подготовил три варианта. Посмотри условия и сразу одобряю для генерации.",
                "goal": "meta",
            }

        data = {
            "courseId": "c1",
            "conversation": [{"role": "user", "content": user}],
            "memory": {"latestIntentKind": "generate", "latestExplicitInstruction": user},
        }
        result = {
            "assistantMessage": "Сохраняю условия",
            "actions": [{
                "name": "save_chat_blueprint",
                "arguments": {
                    "courseId": "c1",
                    "summary": "Три обучалки по if",
                    "proposals": [
                        {"title": "Обучалка 1", "fullCondition": "Напишите программу с if."},
                        {"title": "Обучалка 2", "fullCondition": "Напишите программу с if else."},
                        {"title": "Обучалка 3", "fullCondition": "Напишите программу с диапазоном."},
                    ],
                },
            }],
        }

        with patch.dict(os.environ, {"TASKFORGE_DISABLE_LLM_SLOT_EXPANSION": "0"}):
            with patch("worker.call_llm", side_effect=fake_meta_call):
                normalized = worker._normalize_chat_turn_result(data, result)

        proposals = normalized["actions"][0]["arguments"]["proposals"]
        for proposal in proposals[3:]:
            text = (proposal.get("title", "") + "\n" + proposal.get("fullCondition", "")).lower()
            self.assertNotIn("я подготовил", text)
            self.assertNotIn("сохранил", text)
            self.assertNotIn("посмотри условия", text)
            self.assertIn("Следуй шагам", proposal.get("fullCondition", ""))
            self.assertRegex(proposal.get("fullCondition", "").lower(), r"if|условн|провер")
if __name__ == "__main__":
    unittest.main()

