import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore


class BlueprintRoutingHintRegressionsTests(unittest.TestCase):
    def test_build_blueprint_routing_hint_marks_anchor_onboarding_after_assent(self):
        payload = {
            "conversation": [{"role": "user", "content": "Напиши черновики к этим задачам"}],
            "memory": {
                "latestExplicitInstruction": "Давай\nСогласен\nНапиши черновики к этим задачам",
                "recentGoals": [
                    "if появляется резко и без обучалки",
                    "надо сделать задачки, которые пошагово расскажут как if работает",
                ],
            },
        }
        proposals = [
            {"title": "Задание 1. Простой if", "conditionPreview": "Напиши программу с if"},
            {"title": "Задание 2. if + else", "conditionPreview": "Добавь ветку else"},
            {"title": "Задание 3. if + else if + else", "conditionPreview": "Цепочка условий"},
        ]
        hint = worker._chat_build_blueprint_routing_hint(payload, "Напиши черновики к этим задачам", "", proposals)
        self.assertEqual(hint.get("mode"), "anchor-onboarding")
        self.assertEqual(hint.get("concept"), "if")
        self.assertTrue(hint.get("proposalEvidence"))
        self.assertTrue(hint.get("agreed"))

    def test_normalize_chat_turn_result_attaches_routing_hint_to_blueprint_save(self):
        payload = {
            "courseId": "c1",
            "availableActions": [{"name": "save_chat_blueprint"}],
            "conversation": [{"role": "user", "content": "Сделай 3 задачи по if: сначала простой if, потом if + else, потом else if"}],
            "memory": {
                "latestExplicitInstruction": "Сделай 3 задачи по if: сначала простой if, потом if + else, потом else if",
                "recentGoals": ["if появляется резко и без обучалки"],
            },
        }
        result = {
            "assistantMessage": "Собрал три черновика.",
            "draftBlueprint": {
                "summary": "Три шага по if",
                "proposals": [
                    {"title": "Задание 1. Простой if", "conditionPreview": "Напиши программу с if"},
                    {"title": "Задание 2. if + else", "conditionPreview": "Добавь ветку else"},
                    {"title": "Задание 3. if + else if + else", "conditionPreview": "Цепочка условий"},
                ],
            },
            "count": 3,
        }
        normalized = worker._normalize_chat_turn_result(payload, result)
        action = normalized["actions"][0]
        self.assertEqual(action["name"], "save_chat_blueprint")
        self.assertEqual(action["arguments"]["routingHint"]["mode"], "anchor-onboarding")
        self.assertEqual(action["arguments"]["routingHint"]["concept"], "if")

    def test_pre_anchor_request_does_not_get_onboarding_hint_without_explicit_proposals(self):
        payload = {
            "conversation": [{"role": "user", "content": "Напиши черновики к этим задачам"}],
            "memory": {
                "latestExplicitInstruction": "В задачках до for не может быть for",
                "recentGoals": ["Нужна подводящая лесенка до первого for"],
            },
        }
        proposals = [
            {"title": "Сравни два числа", "conditionPreview": "Без циклов"},
            {"title": "Посчитай диапазон", "conditionPreview": "Без циклов"},
        ]
        hint = worker._chat_build_blueprint_routing_hint(payload, "Напиши черновики к этим задачам", "", proposals)
        self.assertEqual(hint, {})


if __name__ == "__main__":
    unittest.main()
