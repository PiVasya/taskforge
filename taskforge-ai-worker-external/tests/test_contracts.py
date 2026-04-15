import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore
import repair  # type: ignore
import reviews  # type: ignore
import validators  # type: ignore


LONG_DESC = (
    "Это достаточно длинное описание задания для проверки канонического AI-контракта. "
    "Оно описывает входные данные, ожидаемый результат, ограничения и несколько важных деталей, "
    "чтобы self-check не падал только из-за слишком короткого description. "
    "Дополнительно здесь есть пояснение про крайние случаи и ожидаемую детерминированность решения."
)


def valid_code_test_draft(**overrides):
    draft = {
        "assignmentType": "code-test",
        "title": "Echo number",
        "description": LONG_DESC,
        "publicTests": [
            {"input": "1\n", "expectedOutput": "1"},
            {"input": "2\n", "expectedOutput": "2"},
        ],
        "hiddenTests": [
            {"input": "3\n", "expectedOutput": "3"},
            {"input": "10\n", "expectedOutput": "10"},
            {"input": "42\n", "expectedOutput": "42"},
            {"input": "99\n", "expectedOutput": "99"},
            {"input": "123\n", "expectedOutput": "123"},
        ],
        "referenceSolutionPython": (
            "import sys\n"
            "def solve():\n"
            "    data = sys.stdin.read().strip()\n"
            "    print(data)\n"
            "if __name__ == '__main__':\n"
            "    solve()\n"
        ),
        "requiredCalls": [],
        "forbiddenCalls": [],
    }
    draft.update(overrides)
    return draft


def valid_test_draft(**overrides):
    draft = {
        "assignmentType": "test",
        "title": "Basic quiz",
        "description": LONG_DESC,
        "settings": {
            "maxAttempts": 1,
            "passPercent": 60,
            "shuffleQuestions": True,
            "shuffleAnswers": True,
            "allowReview": True,
            "attemptTimeLimitsSeconds": [],
        },
        "questions": [
            {
                "type": "single-choice",
                "prompt": f"Question {i}",
                "options": [{"key": "a", "text": "A"}, {"key": "b", "text": "B"}],
                "correctOptionKeys": ["a"],
            }
            for i in range(1, 6)
        ],
    }
    draft.update(overrides)
    return draft


def valid_math_draft(**overrides):
    draft = {
        "assignmentType": "math",
        "title": "Simple math",
        "description": LONG_DESC,
        "settings": {
            "maxAttempts": 1,
            "passPercent": 60,
            "shuffleBlocks": False,
            "allowReview": True,
            "attemptTimeLimitsSeconds": [],
        },
        "blocks": [
            {"kind": "info", "prompt": "Read this", "promptContentJson": None, "score": 0, "isRequired": True},
            {
                "kind": "number",
                "prompt": "2 + 2 = ?",
                "promptContentJson": None,
                "score": 1,
                "isRequired": True,
                "acceptedAnswers": ["4"],
                "caseSensitive": False,
                "trim": True,
                "numericTolerance": 0,
            },
        ],
    }
    draft.update(overrides)
    return draft


class ContractTests(unittest.TestCase):
    def test_code_policy_nested_is_rejected(self):
        draft = valid_code_test_draft(codePolicy={"requiredCalls": ["solve"], "forbiddenCalls": []})
        result = validators.run_self_check(draft)
        checks = {c["name"]: c for c in result["checks"]}
        self.assertEqual(checks["code-policy-shape"]["status"], "failed")

    def test_payload_does_not_force_allowed_languages_when_absent(self):
        wrapped = payload._synthesize_generation_result(  # noqa: SLF001
            {"assignmentType": "code-test", "schemaVersion": "draft-v2"},
            {"draft": valid_code_test_draft()},
        )
        self.assertNotIn("allowedLanguages", wrapped["draft"])
        self.assertEqual(wrapped["schemaVersion"], "draft-v2")

    def test_math_review_uses_kind_for_answer_blocks(self):
        result = reviews.run_test_strength_review({"draft": valid_math_draft()}, {"targetEntityId": "draft-1"})
        checks = {c["name"]: c for c in result["checks"]}
        self.assertEqual(checks["answer-blocks"]["status"], "passed")
        self.assertEqual(checks["blocks-count"]["status"], "passed")

    def test_test_review_detects_shape(self):
        result = reviews.run_test_strength_review({"draft": valid_test_draft()}, {"targetEntityId": "draft-2"})
        checks = {c["name"]: c for c in result["checks"]}
        self.assertEqual(checks["questions-count"]["status"], "passed")
        self.assertEqual(checks["question-shape"]["status"], "passed")

    def test_fallback_repair_returns_schema_version_without_fabricating_tests(self):
        draft = valid_code_test_draft(hiddenTests=[{"input": "3\n", "expectedOutput": "3"}])
        repaired = repair.fallback_repair_result(
            {"schemaVersion": "draft-v2", "draft": draft, "reviewResults": [], "repairPlan": {}, "scorecard": {}},
            {"type": "assignment_repair"},
        )
        self.assertEqual(repaired["schemaVersion"], "draft-v2")
        self.assertEqual(len(repaired["draft"]["hiddenTests"]), 1)


    def test_repair_coerces_top_level_draft_shape(self):
        repaired = repair._coerce_repair_result(  # noqa: SLF001
            {"type": "assignment_generate_from_text"},
            {"assignmentType": "code-test"},
            valid_code_test_draft(title="Квадрат числа с префиксом"),
        )
        self.assertIsNotNone(repaired)
        self.assertIn("draft", repaired)
        self.assertEqual(repaired["draft"]["title"], "Квадрат числа")

    def test_batch_context_review_does_not_fail_on_implicit_skill_anchor(self):
        draft = valid_code_test_draft(title="Квадрат числа", description=LONG_DESC + " В задаче нужно считать одно число и вывести его квадрат.")
        review = reviews.run_batch_context_review(
            {
                "draft": draft,
                "batchItemContext": {"targetSkill": "C++: базовый ввод и вывод — Одно значение и печать", "difficultyTarget": 1},
                "batchPeerDrafts": [],
            },
            {"targetEntityId": "draft-3"},
        )
        self.assertEqual(review["status"], "passed")


    def test_self_check_flags_missing_echo_value_in_outputs(self):
        draft = valid_code_test_draft(
            title="Echo with prefix",
            description=LONG_DESC + " Считайте одно целое число. Вывод: Строка 'Вы ввели: ' и само число.",
            publicTests=[
                {"input": "1\n", "expectedOutput": "Вы ввели:"},
                {"input": "10\n", "expectedOutput": "Вы ввели:"},
            ],
            hiddenTests=[],
            referenceSolutionPython=(
                "import sys\n"
                "def solve():\n"
                "    data = sys.stdin.read().strip()\n"
                "    if data:\n"
                "        print(f'Вы ввели: {data}')\n"
                "if __name__ == '__main__':\n"
                "    solve()\n"
            ),
        )
        result = validators.run_self_check(draft)
        checks = {c["name"]: c for c in result["checks"]}
        self.assertEqual(checks["input-value-preserved-in-output"]["status"], "failed")
        self.assertNotEqual(result["status"], "passed")


if __name__ == "__main__":
    unittest.main()
