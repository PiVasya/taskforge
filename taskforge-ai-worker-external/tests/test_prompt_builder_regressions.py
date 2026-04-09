import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import prompt_builder  # type: ignore


LONG_DESC = (
    "Это достаточно длинное описание задания для проверки регрессионного сценария. "
    "Нужно убедиться, что prompt builder больше не падает из-за перепутанного порядка аргументов. "
    "Описание специально длиннее минимального порога и содержит несколько фраз про вход, вывод и ограничения."
)


class PromptBuilderRegressionTests(unittest.TestCase):
    def test_build_draft_generate_prompt_accepts_dict_payload(self):
        payload = {
            "assignmentType": "code-test",
            "schemaVersion": "draft-v2",
            "prompt": "Сгенерируй одну задачу на базовый ввод и вывод в C++",
            "titleHint": "Одно значение и печать",
            "targetSkill": "C++: базовый ввод и вывод — Одно значение и печать",
            "microGoal": "Считать одно значение и вывести его в требуемом формате.",
            "difficulty": 1,
            "count": 1,
            "qualityGates": {},
            "task": {
                "titleHint": "Одно значение и печать",
                "targetSkill": "C++: базовый ввод и вывод — Одно значение и печать",
                "microGoal": "Считать одно значение и вывести его в требуемом формате.",
                "difficultyTarget": 1,
            },
            "courseProfile": {
                "summary": "Минимальный профиль курса",
                "courseDigest": {"languages": ["cpp"]},
                "courseProfile": {},
            },
            "gapAnalysis": {
                "summary": "Минимальный gap analysis",
                "gapAnalysis": {"missingTopics": ["formatted output"]},
                "coverage": {},
            },
        }
        prompt = prompt_builder.build_draft_generate_prompt({"type": "assignment_generate_from_text"}, payload)
        self.assertIn("TaskForge AI", prompt)
        self.assertIn("Одно значение и печать", prompt)


if __name__ == "__main__":
    unittest.main()
