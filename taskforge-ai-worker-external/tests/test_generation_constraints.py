import unittest

from payload import sanitize_result_payload
from validators import run_self_check


class GenerationConstraintTests(unittest.TestCase):
    def test_cpp_prompt_narrows_allowed_languages_and_self_check_passes_dynamic_tests(self):
        payload = {
            "assignmentType": "code-test",
            "prompt": "Сгенерируй задачу по основам C++ и базовому вводу-выводу",
            "supportedLanguages": ["cpp", "csharp", "python"],
            "qualityGates": {
                "minPublicTests": 2,
                "minHiddenTests": 1,
                "minTotalTests": 5,
                "preferPublicTestsMoreThanHidden": True,
            },
        }
        result = {
            "draft": {
                "assignmentType": "code-test",
                "title": "Тест",
                "description": "Условие. " * 40,
                "allowedLanguages": ["cpp", "python"],
                "publicTests": [
                    {"input": "1\n", "expectedOutput": "1"},
                    {"input": "2\n", "expectedOutput": "2"},
                    {"input": "5\n", "expectedOutput": "5"},
                ],
                "hiddenTests": [
                    {"input": "0\n", "expectedOutput": "0"},
                    {"input": "-1\n", "expectedOutput": "-1"},
                ],
                "referenceSolutionPython": "def solve():\n    import sys\n    data = sys.stdin.read().strip()\n    if data:\n        print(data)\n\nif __name__ == '__main__':\n    solve()",
            }
        }
        sanitized = sanitize_result_payload("assignment_generate_from_text", payload, result)
        self.assertEqual(sanitized["draft"]["allowedLanguages"], ["cpp"])
        validation = run_self_check(sanitized["draft"])
        self.assertEqual(validation["status"], "passed")

    def test_sanitize_rebalances_tests_and_normalizes_title(self):
        payload = {
            "assignmentType": "code-test",
            "prompt": "Сгенерируй задачу по основам C++ и базовому вводу-выводу",
            "supportedLanguages": ["cpp"],
            "qualityGates": {
                "minPublicTests": 2,
                "minHiddenTests": 1,
                "minTotalTests": 5,
                "preferPublicTestsMoreThanHidden": True,
            },
            "referenceAssignments": [
                {"title": "Квадрат числа"},
                {"title": "Сумма чисел"},
            ],
        }
        result = {
            "draft": {
                "assignmentType": "code-test",
                "title": "Квадрат числа с фиксированным форматом",
                "description": "Условие. " * 40,
                "allowedLanguages": ["cpp"],
                "publicTests": [
                    {"input": "1\n", "expectedOutput": "1"},
                    {"input": "2\n", "expectedOutput": "4"},
                ],
                "hiddenTests": [
                    {"input": "0\n", "expectedOutput": "0"},
                    {"input": "-1\n", "expectedOutput": "1"},
                    {"input": "3\n", "expectedOutput": "9"},
                ],
                "referenceSolutionPython": (
                    "def solve():\n"
                    "    import sys\n"
                    "    data = sys.stdin.read().strip()\n"
                    "    if data:\n"
                    "        n = int(data)\n"
                    "        print(n * n)\n\n"
                    "if __name__ == '__main__':\n"
                    "    solve()\n"
                ),
            }
        }
        sanitized = sanitize_result_payload("assignment_generate_from_text", payload, result)
        self.assertEqual(sanitized["draft"]["title"], "Квадрат числа")
        self.assertGreater(len(sanitized["draft"]["publicTests"]), len(sanitized["draft"]["hiddenTests"]))


if __name__ == "__main__":
    unittest.main()
