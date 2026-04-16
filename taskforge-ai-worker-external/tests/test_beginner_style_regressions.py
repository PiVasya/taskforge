import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore
import validators  # type: ignore


class BeginnerStyleRegressionsTests(unittest.TestCase):
    def test_style_contract_is_propagated_from_anchor_context(self):
        payload_data = {
            "assignmentType": "code-test",
            "anchorContext": {
                "exactStyleRequested": True,
                "styleExemplarAssignments": [{"title": "Задание 1. Твой первый вывод"}],
            },
            "qualityGates": {"minPublicTests": 1, "minHiddenTests": 0, "minTotalTests": 1},
        }
        result = payload._synthesize_generation_result(payload_data, {
            "draft": {
                "assignmentType": "code-test",
                "title": "Задание X.1",
                "description": "Давай начнём.\n\nСледуй шагам:\n1. ...",
                "publicTests": [{"input": "", "expectedOutput": "A"}],
                "hiddenTests": [],
                "referenceSolutionPython": "def solve():\n    print(\"A\")\n\nif __name__ == '__main__':\n    solve()",
            }
        })
        meta = result["draft"].get("meta") or {}
        contract = meta.get("styleContract") or {}
        self.assertTrue(contract.get("exactStyleRequested"))
        self.assertTrue(contract.get("preferGuidedIntroScaffold"))

    def test_validator_flags_generic_commentary_for_exact_style(self):
        draft = {
            "assignmentType": "code-test",
            "title": "Задание X.4. Ввод слова",
            "description": "Объяви char input[20]; Напиши cin >> input;. Это самый простой способ получить одно слово от пользователя.",
            "publicTests": [{"input": "abc", "expectedOutput": "abc"}],
            "hiddenTests": [],
            "referenceSolutionPython": "def solve():\n    import sys\n    data = sys.stdin.read().strip().split()\n    print(data[0] if data else '')\n\nif __name__ == '__main__':\n    solve()",
            "meta": {
                "qualityGates": {"minPublicTests": 1, "minHiddenTests": 0, "minTotalTests": 1},
                "styleContract": {"exactStyleRequested": True, "preferGuidedIntroScaffold": True, "avoidGenericCommentary": True},
            },
        }
        validation = validators.validate_code_test_draft(draft)
        failed = {c["name"] for c in validation["checks"] if c.get("status") == "failed"}
        self.assertIn("style-generic-commentary", failed)
        self.assertIn("style-guided-scaffold", failed)


if __name__ == "__main__":
    unittest.main()
