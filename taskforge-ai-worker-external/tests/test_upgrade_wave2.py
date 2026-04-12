import os
import sys
import time
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore
import prompt_builder  # type: ignore
import repair  # type: ignore
import runners  # type: ignore


LONG_DESC = (
    "Это достаточно длинное описание задания для проверки route-aware repair и batch-plan validator. "
    "Оно описывает вход, выход, ограничения и несколько крайних случаев, чтобы self-check не падал из-за длины. "
    "Дополнительно текст помогает проверить, что fallback repair не ломает тему задания."
)


def valid_code_test_draft(**overrides):
    draft = {
        "assignmentType": "code-test",
        "title": "Echo number",
        "description": LONG_DESC,
        "publicTests": [
            {"input": "1\n", "expectedOutput": "1"},
        ],
        "hiddenTests": [
            {"input": "2\n", "expectedOutput": "2"},
            {"input": "3\n", "expectedOutput": "3"},
        ],
        "referenceSolutionPython": "print(input())\n",
        "requiredCalls": ["solve"],
        "forbiddenCalls": ["solve", "eval"],
    }
    draft.update(overrides)
    return draft


class UpgradeWave2Tests(unittest.TestCase):
    def test_assignment_repair_prompt_mentions_route_directive(self):
        text = prompt_builder.build_job_specific_instructions(
            "assignment_repair",
            {
                "repairPlan": {"primaryRoute": "tests", "routes": ["tests", "policy"]},
                "reviewResults": [{"result": {"findings": [{"message": "Нужно усилить hiddenTests"}]}}],
            },
        )
        self.assertIn("Route-aware repair directive", text)
        self.assertIn("hiddenTests", text)

    def test_fallback_repair_route_tests_rebalances_tests_and_cleans_policy(self):
        payload_in = {
            "schemaVersion": "draft-v2",
            "draft": valid_code_test_draft(),
            "reviewResults": [],
            "repairPlan": {"primaryRoute": "tests", "routes": ["tests"]},
            "scorecard": {},
        }
        repaired = repair.fallback_repair_result(payload_in, {"type": "assignment_repair"})
        draft = repaired["draft"]
        self.assertGreaterEqual(len(draft["publicTests"]), 2)
        self.assertGreaterEqual(len(draft["hiddenTests"]), 1)
        self.assertNotIn("solve", draft["forbiddenCalls"])

    def test_fallback_repair_route_solution_wraps_reference_solution(self):
        payload_in = {
            "schemaVersion": "draft-v2",
            "draft": valid_code_test_draft(referenceSolutionPython="x = input()\nprint(x)\n"),
            "reviewResults": [],
            "repairPlan": {"primaryRoute": "solution", "routes": ["solution"]},
            "scorecard": {},
        }
        repaired = repair.fallback_repair_result(payload_in, {"type": "assignment_repair"})
        code = repaired["draft"]["referenceSolutionPython"]
        self.assertIn("def solve():", code)
        self.assertIn("__main__", code)

    def test_batch_plan_validator_warns_unknown_placement_ids(self):
        validation = payload._validate_batch_plan(  # noqa: SLF001
            {"referenceAssignments": [{"id": "known-1"}]},
            {"count": 1, "avoid": ["циклы"]},
            [{
                "targetSkill": "ввод и вывод",
                "microGoal": "Считать число и вывести его",
                "placementAfterAssignmentId": "missing-id",
                "titleHint": "Одно число",
            }],
        )
        checks = {item["name"]: item for item in validation["checks"]}
        self.assertEqual(checks["placement-ids-known"]["status"], "warning")

    def test_runner_timeout_reports_timeout_flag(self):
        old_timeout = runners.RUNNER_TIMEOUT_SECONDS
        try:
            runners.RUNNER_TIMEOUT_SECONDS = 1
            started = time.time()
            result = runners.run_python_solution("while True:\n    pass\n", "")
            self.assertTrue(result["timedOut"])
            self.assertLess(time.time() - started, 4.5)
        finally:
            runners.RUNNER_TIMEOUT_SECONDS = old_timeout


if __name__ == "__main__":
    unittest.main()
