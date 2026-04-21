import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import prompt_builder  # type: ignore


class AnchorModeRoutingRegressionTests(unittest.TestCase):
    def test_explicit_for_onboarding_beats_pre_anchor_fallback(self):
        payload = {
            "prompt": "Проанализируй C++ курс: for появляется резко, нужны маленькие задачки, которые пошагово расскажут сам цикл.",
            "conversation": [{"role": "user", "content": "Сначала просто for, потом for с шагом 2"}],
            "memory": {
                "latestExplicitInstruction": "Давай",
                "recentGoals": [
                    "for появляется резко и без обучалки",
                    "надо сделать задачки, которые пошагово расскажут как for работает, сначала просто for, потом for с шагом 2",
                ],
            },
        }
        self.assertEqual(prompt_builder._extract_anchor_concept(prompt_builder._payload_request_text(payload)), "for")
        self.assertFalse(prompt_builder._is_pre_anchor_scaffolding_request(payload))
        self.assertTrue(prompt_builder._is_anchor_onboarding_request(payload))

    def test_strong_pre_for_marker_wins(self):
        payload = {
            "prompt": "Нужна подготовка до for. В задачках до for не может быть for.",
            "conversation": [{"role": "user", "content": "В задачках до for не может быть for"}],
            "memory": {
                "latestExplicitInstruction": "В задачках до for не может быть for",
                "recentGoals": ["Нужна подготовительная лесенка до первого for"],
            },
        }
        self.assertEqual(prompt_builder._extract_anchor_concept(prompt_builder._payload_request_text(payload)), "for")
        self.assertTrue(prompt_builder._is_pre_anchor_scaffolding_request(payload))
        self.assertFalse(prompt_builder._is_anchor_onboarding_request(payload))

    def test_batch_plan_prompt_mentions_generic_anchor_for_pre_scaffold(self):
        payload = {
            "prompt": "Проанализируй C++ курс: for появляется резко и без обучалки, нужны подводящие маленькие задачи до темы for.",
            "conversation": [{"role": "user", "content": "Нужна лесенка до первого for, без самого for в условиях"}],
            "memory": {
                "latestExplicitInstruction": "Нужна лесенка до первого for, без самого for в условиях",
                "recentGoals": ["for появляется резко и без обучалки"],
            },
        }
        prompt = prompt_builder.build_batch_plan_prompt({"type": "assignment_batch_plan"}, payload)
        self.assertIn("ДО первого for", prompt)
        self.assertIn("без явного for", prompt)

    def test_batch_plan_prompt_mentions_generic_anchor_for_onboarding(self):
        payload = {
            "prompt": "Сделай лесенку по самому for: первый шаг уже с for, потом ещё пара микрошагов.",
            "conversation": [{"role": "user", "content": "Нужна серия маленьких программ: первый шаг уже for"}],
            "memory": {
                "latestExplicitInstruction": "Нужна серия маленьких программ: первый шаг уже for",
            },
        }
        prompt = prompt_builder.build_batch_plan_prompt({"type": "assignment_batch_plan"}, payload)
        self.assertIn("обучающую лесенку по for", prompt)
        self.assertNotIn("ДО первого for", prompt)

    def test_diagnostics_explain_haystack_explicit_beats_pre(self):
        payload = {
            "prompt": "Проанализируй C++ курс: if появляется резко и без обучалки.",
            "conversation": [{"role": "user", "content": "Сначала просто if, потом if else"}],
            "memory": {
                "latestExplicitInstruction": "Напиши черновики к этим задачам",
                "recentGoals": [
                    "if появляется резко и без обучалки",
                    "надо сделать задачки, которые пошагово расскажут как if работает, сначала просто if, потом if else",
                ],
            },
        }
        diag = prompt_builder._anchor_routing_diagnostics(payload)
        self.assertEqual(diag.get("mode"), "anchor-onboarding")
        self.assertFalse(diag.get("explicitLatest"))
        self.assertTrue(diag.get("explicitHaystack"))
        self.assertFalse(diag.get("preAnchor"))

if __name__ == "__main__":
    unittest.main()
