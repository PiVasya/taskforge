import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore


class BatchMemoryRegressionTests(unittest.TestCase):
    def test_infer_requested_domain_prefers_batch_memory_for_beginner_bridges(self):
        inferred = payload._infer_requested_domain({  # noqa: SLF001
            "prompt": "Сделай мягкие мостики перед циклами",
            "batchMemory": {
                "userIntentSummary": "Добавить очень простые пошаговые обучалки по вводу, выводу, if и типам данных до циклов",
                "constraints": {"mustStayBeforeConcepts": ["циклы"], "avoidConcepts": ["циклы"]},
                "agentState": {
                    "userIntentSummary": "Подготовить базу перед циклами для новичков",
                    "activeConstraints": ["циклы", "очень просто"],
                    "placementCandidates": [
                        {"concept": "ввод и вывод", "afterAssignmentTitle": "Задание 1. Вывод через cout", "reason": "сначала укрепить базу"}
                    ],
                },
            },
        })
        self.assertEqual(inferred, "bridge-pack")

    def test_synthesize_batch_plan_uses_agent_state_placement_candidates_when_plan_missing(self):
        repaired = payload._synthesize_batch_plan(  # noqa: SLF001
            {
                "count": 2,
                "difficulty": 1,
                "prompt": "Нужны подводящие задачи перед циклами",
                "batchMemory": {
                    "userIntentSummary": "Нужны мягкие мостики перед циклами",
                    "pedagogy": {"preferGuidedWalkthroughs": True},
                    "agentState": {
                        "userIntentSummary": "Сначала объяснить ввод-вывод и if, потом перейти к циклам",
                        "placementCandidates": [
                            {
                                "source": "bridge-plan",
                                "concept": "базовый ввод и вывод",
                                "afterAssignmentId": "a1",
                                "afterAssignmentTitle": "Задание 1. Вывод через cout",
                                "reason": "перед переходом к новой теме",
                                "taskCount": 2,
                                "difficulty": 1,
                                "taskFormat": "guided-walkthrough",
                                "titleHint": "Первое знакомство с вводом",
                            }
                        ],
                    },
                },
            },
            {"canonicalRequest": {"count": 2, "difficulty": 1, "mustInclude": ["cin", "cout"], "avoid": ["циклы"]}},
        )
        tasks = (((repaired or {}).get("plan") or {}).get("tasks") or [])
        self.assertEqual(len(tasks), 2)
        self.assertEqual(tasks[0]["placementAfterAssignmentId"], "a1")
        self.assertEqual(tasks[0]["taskFormat"], "guided-walkthrough")
        self.assertIn("ввод", tasks[0]["targetSkill"])


if __name__ == "__main__":
    unittest.main()
