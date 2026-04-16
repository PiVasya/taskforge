import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore
import worker  # type: ignore


class StyleExemplarSelectionTests(unittest.TestCase):
    def test_draft_stage_keeps_first_task_as_style_exemplar_when_user_requests_one_to_one(self):
        data = {
            "assignmentType": "code-test",
            "prompt": "Сделай в стиле первого задания курса, 1 в 1 как первое задание, но про char.",
            "sourceText": "Нужно повторить первое задание 1 в 1.",
            "referenceAssignments": [
                {"id": "a1", "title": "Задание 1. Твой первый вывод", "description": "Следуй шагам: 1. Напиши #include ...", "difficulty": 2, "sort": 0},
                {"id": "a2", "title": "Задание 8. Что-то ещё", "description": "Другое задание", "difficulty": 2, "sort": 8},
                {"id": "a3", "title": "Задание 14.2. Перед char[]", "description": "Anchor", "difficulty": 2, "sort": 42},
            ],
            "task": {
                "targetSkill": "char",
                "microGoal": "первое знакомство с char[]",
                "placementAfterAssignmentId": "a3",
                "placementAfterTitle": "Задание 14.2. Перед char[]",
            },
        }
        compact = payload.compact_payload_for_stage("draft_body_generate", data)
        refs = compact.get("referenceAssignments") or []
        self.assertTrue(any(item.get("id") == "a1" for item in refs))
        anchor_context = compact.get("anchorContext") or {}
        exemplars = anchor_context.get("styleExemplarAssignments") or []
        self.assertTrue(any(item.get("id") == "a1" for item in exemplars))
        self.assertTrue(anchor_context.get("exactStyleRequested"))


class StyleInspectionRoutingTests(unittest.TestCase):
    def test_style_inspection_signal_detects_first_task_request(self):
        self.assertTrue(worker._chat_requests_style_exemplar_inspection("открой первую задачу и повтори стиль 1 в 1"))

    def test_existing_inspection_satisfies_style_request(self):
        data = {
            "memory": {
                "lastCourseInspection": {
                    "assignments": [
                        {"id": "a1", "title": "Задание 1. Твой первый вывод", "sort": 0},
                        {"id": "a8", "title": "Задание 8.4. Пример", "sort": 11},
                    ]
                }
            }
        }
        self.assertTrue(worker._chat_has_matching_style_inspection(data, "открой первую задачу и сделай как первое задание"))


if __name__ == "__main__":
    unittest.main()
