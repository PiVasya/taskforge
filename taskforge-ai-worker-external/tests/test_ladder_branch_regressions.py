import os
import sys
import textwrap
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import ladder_style  # type: ignore
import validators  # type: ignore
import worker  # type: ignore


class LadderBranchRegressionsTests(unittest.TestCase):
    def test_visual_preview_intent_is_detected(self):
        text = "Придумай лесенку, задачи не создавай, просто наглядно покажи схему"
        self.assertTrue(worker._chat_is_visual_preview_request(text))
        self.assertEqual(worker._chat_latest_intent_kind({"memory": {}}, text, text), "concept-preview")

    def test_chat_blueprint_draft_request_is_detected(self):
        payload = {"memory": {}}
        text = "Давай. Согласен. Напиши черновики к этим задачам по if в таком стиле"
        self.assertTrue(worker._chat_is_chat_blueprint_request(payload, text))
        self.assertEqual(worker._chat_latest_intent_kind(payload, text, text), "show-blueprint")

    def test_audit_request_detects_if_and_branching_language(self):
        text = "Проанализируй курс: if появляется резко, без объяснения ветвления и условий"
        self.assertTrue(worker._chat_is_audit_request(text))

    def test_ladder_style_heuristic_likes_screenshot_like_scaffold(self):
        draft = {
            "title": "Задание 1. Твой первый вывод",
            "description": textwrap.dedent(
                """
                Давай напишем твою первую программу на C++.

                Следуй шагам:
                1. Напиши #include <iostream>
                (Эта строка подключает библиотеку.)
                2. Напиши using namespace std;
                (Так короче работать с cout.)
                3. Напиши int main() {
                (Это начало программы.)
                4. Внутри напиши cout << "Hi";
                (Так программа выведет текст.)
                5. Закрой программу скобкой }

                Запусти код и посмотри, как появится приветствие.
                """
            ).strip(),
        }
        self.assertTrue(ladder_style.looks_like_ladder_style(draft))
        self.assertFalse(ladder_style.looks_too_dry_for_ladder(draft))

    def test_exact_style_validator_requires_finish_line_and_explanations(self):
        draft = {
            "assignmentType": "code-test",
            "title": "Задание X",
            "description": textwrap.dedent(
                """
                Давай начнём.

                Следуй шагам:
                1. Напиши cout << 1;
                2. Добавь ещё одну строку.
                3. Запусти программу.
                """
            ).strip(),
            "publicTests": [{"input": "", "expectedOutput": "1"}],
            "hiddenTests": [],
            "referenceSolutionPython": textwrap.dedent(
                """
                def solve():
                    print(1)

                if __name__ == '__main__':
                    solve()
                """
            ).strip(),
            "meta": {
                "qualityGates": {"minPublicTests": 1, "minHiddenTests": 0, "minTotalTests": 1},
                "styleContract": {"preferGuidedIntroScaffold": True},
            },
        }
        validation = validators.validate_code_test_draft(draft)
        failed = {c["name"] for c in validation["checks"] if c.get("status") == "failed"}
        self.assertIn("style-guided-scaffold", failed)


if __name__ == "__main__":
    unittest.main()
