import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore
from similarity_signatures import similarity_signature_report  # type: ignore


class SimilaritySignatureTests(unittest.TestCase):
    def test_signature_report_detects_near_duplicate(self):
        a = "Считать n и вывести сумму чисел от 1 до n с использованием цикла for"
        b = "Прочитайте число n и напечатайте сумму от 1 до n, используя цикл for"
        report = similarity_signature_report(a, b)
        self.assertGreater(report["combined"], 0.55)
        self.assertGreaterEqual(report["simhashSimilarity"], 0.5)

    def test_anchor_context_includes_signature_hints(self):
        ctx = payload._build_anchor_context(  # noqa: SLF001
            {
                "prompt": "Сделай задачу про сумму от 1 до n с циклом for",
                "task": {"targetSkill": "циклы", "microGoal": "сумма от 1 до n"},
            },
            [
                {"id": "a1", "title": "Сумма от 1 до n", "descriptionSummary": "Прочитайте n и найдите сумму от 1 до n через цикл for", "difficulty": 1},
                {"id": "a2", "title": "Минимум массива", "descriptionSummary": "Найдите минимальный элемент массива", "difficulty": 1},
            ],
        )
        hints = ctx.get("duplicateSignatureHints") or []
        self.assertTrue(hints)
        self.assertEqual(hints[0]["id"], "a1")


if __name__ == "__main__":
    unittest.main()
