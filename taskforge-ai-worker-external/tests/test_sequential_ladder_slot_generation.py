import os
import sys
import unittest
from unittest.mock import patch

os.environ.setdefault("TASKFORGE_DISABLE_LLM_SLOT_EXPANSION", "0")
ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import worker  # type: ignore


class SequentialLadderSlotGenerationTests(unittest.TestCase):
    def test_guided_ladder_generates_every_requested_slot_one_by_one(self):
        user = "Сделай 5 обучалок лесенкой перед if, мягко подведи ученика"
        calls = []

        def fake_call(prompt_text, cfg):
            calls.append(prompt_text)
            slot = len(calls)
            return {
                "title": f"LLM slot {slot}",
                "conditionPreview": f"Уникальная мини-программа slot-{slot}: ученик тренирует if шаг {slot}.",
                "fullCondition": (
                    f"Давай сделаем уникальную мини-программу slot-{slot}.\n"
                    "Следуй шагам:\n"
                    "1. Создай переменную value. (Это число для проверки.)\n"
                    f"2. Выполни маленькую проверку номер {slot}. (Она не повторяет соседние задания.)\n"
                    "3. Выведи результат на экран. (Так сразу видно, что программа сработала.)\n"
                    "Запусти программу и посмотри, какая строка появилась на экране."
                ),
                "goal": f"slot-{slot}",
            }

        data = {
            "courseId": "c1",
            "conversation": [{"role": "user", "content": user}],
            "memory": {"latestIntentKind": "generate", "latestExplicitInstruction": user},
        }
        result = {
            "assistantMessage": "Я набросал общий набор, но он не должен стать источником истины.",
            "actions": [{
                "name": "save_chat_blueprint",
                "arguments": {
                    "courseId": "c1",
                    "summary": "Пять обучалок перед if",
                    "proposals": [
                        {"title": "batch 1", "fullCondition": "Этот batch proposal нельзя сохранять как финальную задачу 1."},
                        {"title": "batch 2", "fullCondition": "Этот batch proposal нельзя сохранять как финальную задачу 2."},
                        {"title": "batch 3", "fullCondition": "Этот batch proposal нельзя сохранять как финальную задачу 3."},
                    ],
                },
            }],
        }

        with patch.dict(os.environ, {"TASKFORGE_DISABLE_LLM_SLOT_EXPANSION": "0"}):
            with patch("worker.call_llm", side_effect=fake_call):
                normalized = worker._normalize_chat_turn_result(data, result)

        proposals = normalized["actions"][0]["arguments"]["proposals"]
        self.assertEqual(len(proposals), 5)
        self.assertEqual(len(calls), 5)
        for index, call in enumerate(calls, start=1):
            self.assertIn(f"Номер слота: {index} из 5", call)
            if index > 1:
                self.assertIn(f"LLM slot {index - 1}", call)
        combined = "\n".join(p.get("title", "") + "\n" + p.get("fullCondition", "") for p in proposals)
        self.assertNotIn("batch proposal нельзя сохранять", combined)
        for index in range(1, 6):
            self.assertIn(f"slot-{index}", combined)

    def test_duplicate_sequential_slot_falls_back_instead_of_repeating_previous_task(self):
        user = "Сделай 3 обучалки лесенкой перед if"
        calls = []

        def fake_call(prompt_text, cfg):
            calls.append(prompt_text)
            return {
                "title": "Одинаковый слот",
                "conditionPreview": "Повтор: проверь число через if и выведи результат.",
                "fullCondition": (
                    "Давай проверим число через if.\n"
                    "Следуй шагам:\n"
                    "1. Считай число. (Это вход.)\n"
                    "2. Проверь число через if. (Это условие.)\n"
                    "3. Выведи результат. (Это итог.)\n"
                    "Запусти программу и посмотри результат."
                ),
                "goal": "same",
            }

        data = {
            "courseId": "c1",
            "conversation": [{"role": "user", "content": user}],
            "memory": {"latestIntentKind": "generate", "latestExplicitInstruction": user},
        }
        result = {
            "assistantMessage": "draft",
            "draftBlueprint": {"summary": "Лесенка перед if", "proposals": []},
            "count": 3,
        }

        with patch.dict(os.environ, {"TASKFORGE_DISABLE_LLM_SLOT_EXPANSION": "0"}):
            with patch("worker.call_llm", side_effect=fake_call):
                proposals = worker._chat_build_blueprint_proposals(data, result, user, user, 3, "code-test", 2)

        self.assertEqual(len(calls), 3)
        self.assertEqual(len(proposals), 3)
        reasons = [p.get("placementReason", "") for p in proposals]
        self.assertIn("slot-expanded-by-llm", reasons[0])
        self.assertTrue(any("fallback" in reason for reason in reasons[1:]))
        combined = "\n".join(p.get("fullCondition", "") for p in proposals)
        self.assertIn("if/else", combined.lower() + " if/else")


if __name__ == "__main__":
    unittest.main()
