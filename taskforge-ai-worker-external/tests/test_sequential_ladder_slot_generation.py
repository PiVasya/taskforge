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
                "conditionPreview": f"Уникальная мини-программа slot-{slot}: ученик тренирует сравнения шаг {slot}.",
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
                self.assertIn("Уже собранные предыдущие слоты", call)
        combined = "\n".join(p.get("title", "") + "\n" + p.get("fullCondition", "") for p in proposals)
        self.assertNotIn("batch proposal нельзя сохранять", combined)
        self.assertIn("slot-1", combined)
        self.assertIn("Задание 5", combined)

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
        self.assertTrue(all("fallback" in reason for reason in reasons))
        combined = "\n".join(p.get("fullCondition", "") for p in proposals)
        self.assertNotRegex(combined, r"(?<![A-Za-zА-Яа-я0-9_])if(?![A-Za-zА-Яа-я0-9_])")
        self.assertNotRegex(combined, r"(?<![A-Za-zА-Яа-я0-9_])else(?![A-Za-zА-Яа-я0-9_])")

    def test_cpp_pre_if_ladder_rejects_python_broken_slots_and_locks_placement(self):
        user = "Сделай 5 обучалок лесенкой перед первым if для курса C++"
        before_anchor_id = "00000000-0000-0000-0000-000000000019"
        wrong_id = "00000000-0000-0000-0000-000000000225"
        data = {
            "courseId": "cpp-course",
            "selectedCourse": {"id": "cpp-course", "title": "Основы C++"},
            "conversation": [{"role": "user", "content": user}],
            "memory": {
                "latestIntentKind": "generate",
                "latestExplicitInstruction": user,
                "lastCourseInspection": {
                    "courseTitle": "Основы C++",
                    "summary": "Первый явный if найден в Задание 20.",
                    "assignments": [
                        {"id": "00000000-0000-0000-0000-000000000018", "title": "Задание 18", "sort": 35, "descriptionExcerpt": "Ввод, вывод и арифметика."},
                        {"id": before_anchor_id, "title": "Задание 19", "sort": 36, "descriptionExcerpt": "Сравнения и булевы выражения без ветвления."},
                        {"id": "00000000-0000-0000-0000-000000000020", "title": "Задание 20", "sort": 37, "descriptionExcerpt": "Используй if для проверки числа."},
                    ],
                },
            },
        }
        bad_proposal = {
            "title": "Python-черновик",
            "conditionPreview": "Считай x = int(input()), затем if/elif и print(True).",
            "fullCondition": "Давай сделаем Python-вариант.\nСледуй шагам: 1\n1. x = int(input())\n2. if x > 0: print(True)\n3. elif x == 0: print(False)",
            "goal": "научиться if/else/elif",
            "placementAfterAssignmentId": wrong_id,
            "placementAfterTitle": "Задание 22.5",
        }
        result = {
            "assistantMessage": "Сохранил blueprint, но это мета-текст и не должен протечь в слоты.",
            "draftBlueprint": {"summary": "5 задач перед if", "proposals": [dict(bad_proposal) for _ in range(5)]},
            "count": 5,
        }

        with patch.dict(os.environ, {"TASKFORGE_DISABLE_LLM_SLOT_EXPANSION": "1"}):
            proposals = worker._chat_build_blueprint_proposals(data, result, user, user, 5, "code-test", 2)

        self.assertEqual(len(proposals), 5)
        combined = "\n".join(
            "\n".join(str(p.get(key) or "") for key in ["title", "goal", "conditionPreview", "fullCondition"])
            for p in proposals
        )
        for forbidden in ["int(input", "input(", "print", "elif", "Python", "Питон", "Следуй шагам: 1", "True", "False"]:
            self.assertNotIn(forbidden, combined)
        self.assertNotRegex(combined, r"(?<![A-Za-zА-Яа-я0-9_])if(?![A-Za-zА-Яа-я0-9_])")
        self.assertNotRegex(combined, r"(?<![A-Za-zА-Яа-я0-9_])else(?![A-Za-zА-Яа-я0-9_])")
        self.assertTrue(all(p.get("placementAfterAssignmentId") == before_anchor_id for p in proposals))
        self.assertTrue(all(p.get("placementAfterTitle") == "Задание 19" for p in proposals))


if __name__ == "__main__":
    unittest.main()
