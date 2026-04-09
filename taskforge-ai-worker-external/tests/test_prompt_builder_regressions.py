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


    def test_build_chat_turn_prompt_mentions_prepare_bridge_plan(self):
        payload = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {"lastCourseAudit": {"summary": "Есть пробелы"}},
            "conversation": [{"role": "user", "content": "собери план мостиков"}],
            "availableActions": [
                {"name": "analyze_course_progression"},
                {"name": "inspect_course_assignments"},
                {"name": "prepare_bridge_plan"},
                {"name": "show_bridge_plan"},
                {"name": "revise_bridge_plan"},
                {"name": "advance_agent_stage"},
                {"name": "queue_generate_bridge_batch"},
            ],
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload)
        self.assertIn("prepare_bridge_plan", prompt)
        self.assertIn("inspect_course_assignments", prompt)
        self.assertIn("show_bridge_plan", prompt)
        self.assertIn("revise_bridge_plan", prompt)
        self.assertIn("advance_agent_stage", prompt)


    def test_build_batch_plan_prompt_uses_batch_memory_for_guided_walkthroughs(self):
        payload = {
            "assignmentType": "code-test",
            "prompt": "Нужно укрепить базу перед циклами",
            "count": 4,
            "difficulty": 1,
            "mode": "topic-pack",
            "referenceAssignments": [
                {"id": "a1", "title": "Задание 1. Вывод через cout", "description": LONG_DESC, "difficulty": 1, "sort": 1, "type": "code-test", "allowedLanguagesCsv": "cpp"},
                {"id": "a2", "title": "Задание 2. Вывод через printf", "description": LONG_DESC, "difficulty": 1, "sort": 2, "type": "code-test", "allowedLanguagesCsv": "cpp"},
            ],
            "batchMemory": {
                "learnerProfile": {"audience": "young-beginners", "explainLikeChild": True, "preferGuidedWalkthroughs": True, "requireSectionIntroGuides": True},
                "pedagogy": {"preferGuidedWalkthroughs": True, "requireSectionIntroGuides": True, "explainLikeChild": True},
                "titleStyle": {"examples": ["Задание 1. Вывод через cout", "Задание 2. Вывод через printf"], "styleHints": ["короткие конкретные названия"]},
                "placementPlan": [{"afterAssignmentId": "a1", "afterAssignmentTitle": "Задание 1. Вывод через cout", "concept": "printf", "reason": "мягко подвести к printf", "taskCount": 2, "difficulty": 1, "taskFormat": "guided-walkthrough"}],
                "constraints": {"mustStayBeforeConcepts": ["циклы"], "avoidConcepts": ["циклы"]},
            },
        }
        prompt = prompt_builder.build_batch_plan_prompt({"type": "assignment_batch_plan"}, payload)
        self.assertIn("batchMemory", prompt)
        self.assertIn("guided-walkthrough", prompt)
        self.assertIn("циклы", prompt)


if __name__ == "__main__":
    unittest.main()
