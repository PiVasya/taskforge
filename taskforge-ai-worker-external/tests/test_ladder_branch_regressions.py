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

    def test_beautify_ladder_proposal_turns_dry_statement_into_friendly_walkthrough(self):
        proposal = {
            "title": "Простой if: проверка условия",
            "conditionPreview": "Считайте целое число. Если введённое число строго больше 0, выведите на экран фразу 'Число положительное'. В остальных случаях программа ничего не должна выводить. Используйте только оператор if.",
            "fullCondition": "Считайте целое число. Если введённое число строго больше 0, выведите на экран фразу 'Число положительное'. В остальных случаях программа ничего не должна выводить. Используйте только оператор if.",
        }

        styled = ladder_style.beautify_ladder_proposal(proposal, "if", 1, 4)

        self.assertIn("Следуй шагам", styled["fullCondition"])
        self.assertIn("Запусти код", styled["fullCondition"])
        self.assertTrue(styled["title"].startswith("Задание 1."))
        self.assertFalse(ladder_style.looks_too_dry_for_ladder({"description": styled["fullCondition"]}))
        self.assertTrue(ladder_style.looks_like_ladder_style({"title": styled["title"], "description": styled["fullCondition"]}))

    def test_chat_blueprint_proposals_force_ladder_style_from_anchor_onboarding_hint(self):
        payload = {
            "conversation": [{"role": "user", "content": "Согласен, давай конкретные черновики"}],
            "memory": {
                "latestExplicitInstruction": "Согласен, давай конкретные черновики",
                "recentGoals": [
                    "Проанализируй C++ курс, там есть задачки на if, но они появляются без введения, тоесть резко и без обучалки, поэтому надо сделать задачки которые пошагово расскажут как if работает, сначала просто if, потом if else, и т.п.",
                ],
            },
        }
        result = {
            "draftBlueprint": {
                "proposals": [
                    {
                        "title": "Вариант 1. Проверка знака числа",
                        "conditionPreview": "Напишите программу, которая считывает одно целое число. Если введённое число строго больше нуля, программа должна вывести на экран слово 'Положительное'. В остальных случаях ничего выводить не нужно. Используйте только оператор if.",
                    },
                    {
                        "title": "Вариант 2. if или нечёт",
                        "conditionPreview": "Напишите программу, которая принимает целое число. Если число делится на 2 без остатка, выведите 'Чётное'. В противном случае выведите 'Нечётное'. Используйте конструкцию if-else.",
                    },
                ]
            },
            "assistantMessage": "Подготовил конкретные черновики.",
        }

        proposals = worker._chat_build_blueprint_proposals(payload, result, payload["conversation"][0]["content"], "Подготовил конкретные черновики.", 2, "code-test", 2)

        self.assertEqual(len(proposals), 2)
        self.assertIn("Следуй шагам", proposals[0]["fullCondition"])
        self.assertIn("Следуй шагам", proposals[1]["fullCondition"])
        self.assertTrue(proposals[0]["title"].startswith("Задание 1."))
        self.assertTrue(proposals[1]["title"].startswith("Задание 2."))

    def test_chat_blueprint_proposals_get_ladder_beautifier_for_dry_drafts(self):
        payload = {
            "conversation": [{"role": "user", "content": "Сделай лесенку по if: сначала простой if, потом if else, потом else if"}],
            "memory": {"latestExplicitInstruction": "Сделай лесенку по if: сначала простой if, потом if else, потом else if"},
        }
        result = {
            "draftBlueprint": {
                "proposals": [
                    {
                        "title": "Простой if: проверка условия",
                        "conditionPreview": "Считайте целое число. Если введённое число строго больше 0, выведите фразу 'Число положительное'. В остальных случаях программа ничего не должна выводить. Используйте только оператор if.",
                    }
                ]
            },
            "assistantMessage": "Собрал черновик лесенки.",
        }

        proposals = worker._chat_build_blueprint_proposals(payload, result, payload["conversation"][0]["content"], "Собрал черновик лесенки.", 1, "code-test", 2)

        self.assertEqual(len(proposals), 1)
        self.assertIn("Следуй шагам", proposals[0]["fullCondition"])
        self.assertTrue(proposals[0]["title"].startswith("Задание 1."))



if __name__ == "__main__":
    unittest.main()
