import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore
import prompt_builder  # type: ignore


class AssignmentOverviewAndChatCompactionTests(unittest.TestCase):
    def test_assignment_overview_is_synthesized_when_model_returns_flat_fields(self):
        raw = {
            "isImportant": True,
            "importanceScore": 0.82,
            "importanceReasons": ["Открывает новую тему курса"],
            "pedagogicalRole": "guided-intro",
            "teachingStyle": "step-by-step",
            "studentStage": "beginner",
            "conceptsIntroduced": ["printf"],
            "conceptsReinforced": [],
            "prerequisites": ["cout"],
            "surfaceSignals": ["printf"],
            "courseValue": "Даёт первое пошаговое знакомство с printf.",
        }
        sanitized = payload.sanitize_result_payload(
            "assignment_analyze_existing",
            {
                "assignment": {
                    "id": "a1",
                    "title": "Задание 1.1. Твой первый printf",
                }
            },
            raw,
        )
        self.assertEqual(sanitized.get("assignmentId"), "a1")
        self.assertEqual(sanitized.get("kind"), "course-overview")
        self.assertTrue(isinstance(sanitized.get("summary"), str) and sanitized.get("summary"))
        self.assertIsInstance(sanitized.get("overview"), dict)
        self.assertEqual(sanitized["overview"].get("pedagogicalRole"), "guided-intro")
        self.assertIn("printf", sanitized["overview"].get("conceptsIntroduced") or [])

    def test_chat_turn_prompt_stays_bounded_for_huge_payload(self):
        payload_in = {
            "sessionId": "s1",
            "courseId": "c1",
            "memory": {
                "summary": "x" * 5000,
                "facts": ["fact " + ("y" * 400)] * 20,
                "recentGoals": ["goal " + ("z" * 300)] * 10,
                "recentActions": ["inspect_course_assignments"] * 10,
            },
            "currentDraftBlueprint": {
                "revision": 3,
                "proposals": [
                    {
                        "id": "p1",
                        "title": "Задание 1.1. Твой первый printf",
                        "conditionPreview": "preview " + ("a" * 1000),
                        "fullCondition": "full " + ("b" * 5000),
                        "difficulty": 1,
                        "publicTests": [{"input": "", "expectedOutput": "printf"}] * 5,
                        "hiddenTests": [{"input": "", "expectedOutput": "printf"}] * 5,
                    }
                ],
            },
            "conversation": [{"role": "user", "content": "сообщение " + ("c" * 3000)} for _ in range(15)],
            "recentAssignments": [{"id": str(i), "title": "Task " + ("d" * 1000), "type": "code-test", "difficulty": 1, "rating": 1, "latestAiOverview": {"summary": "sum " + ("e" * 1000), "importanceReasons": ["r1", "r2"], "conceptsIntroduced": ["cout"]}} for i in range(20)],
            "landmarkAssignments": [{"id": str(i), "title": "Landmark " + ("f" * 1000), "type": "code-test", "difficulty": 1, "rating": 1, "aiOverview": {"summary": "sum " + ("g" * 1000), "importanceReasons": ["r1", "r2"], "conceptsIntroduced": ["printf"], "pedagogicalRole": "guided-intro"}} for i in range(20)],
            "recentJobs": [{"id": str(i), "type": "assignment_analyze_existing", "status": "pending", "errorText": "err " + ("h" * 1000)} for i in range(20)],
            "availableCourses": [{"id": str(i), "title": "Course " + ("i" * 1000)} for i in range(20)],
            "availableActions": [{"name": "inspect_course_assignments", "requiredArguments": ["courseId"], "optionalArguments": ["focus"]}],
            "defaults": {"assignmentType": "code-test", "difficulty": 2, "count": 5, "mode": "topic-pack", "extra": "j" * 2000},
        }
        prompt = prompt_builder.build_chat_turn_prompt({"type": "assistant_chat_turn"}, payload_in)
        self.assertIsInstance(prompt, str)
        self.assertLess(len(prompt), 85000)
        self.assertIn("landmarkAssignments", prompt)
        self.assertIn("guided-intro", prompt)


if __name__ == "__main__":
    unittest.main()
