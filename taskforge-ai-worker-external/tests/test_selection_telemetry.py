import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore


LONG = "Очень длинное описание для выбора reference assignments. " * 10


class SelectionTelemetryTests(unittest.TestCase):
    def test_compact_payload_contains_selection_telemetry(self):
        compact = payload.compact_payload_for_stage(
            "assignment_generate_from_text",
            {
                "assignmentType": "code-test",
                "count": 1,
                "difficulty": 1,
                "prompt": "Сделай простую задачу на ввод и вывод числа",
                "referenceAssignments": [
                    {"id": "a1", "title": "Ввод и вывод", "description": LONG, "difficulty": 1, "sort": 1, "tags": "io, basics"},
                    {"id": "a2", "title": "Сумма двух чисел", "description": LONG, "difficulty": 1, "sort": 2, "tags": "sum, basics"},
                ],
            },
        )
        self.assertIn("selectionTelemetry", compact)
        self.assertIn("selectedIds", compact["selectionTelemetry"])


if __name__ == "__main__":
    unittest.main()
