import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import prompt_builder  # type: ignore
import repair  # type: ignore


class InstructionStrictnessTests(unittest.TestCase):
    def test_contract_extracts_exact_and_forbidden_snippets(self):
        payload = {
            "instructionStrictness": 100,
            "prompt": "Сделай 1 в 1, без return 0",
            "sourceText": "Напишите #include <iostream>\nНапишите using namespace std;",
        }
        contract = prompt_builder._extract_instruction_contract(payload)
        self.assertIn("#include <iostream>", contract["exactSnippets"])
        self.assertTrue(any("return 0" in item for item in contract["forbiddenSnippets"]))
        self.assertTrue(contract["lockScope"])

    def test_instruction_validation_flags_missing_exact_snippet(self):
        payload = {
            "instructionStrictness": 100,
            "prompt": "Сделай по примеру",
            "sourceText": "Напишите #include <iostream>\nБез return 0",
        }
        draft = {
            "title": "Привет!",
            "description": "Напиши #include и using namespace std;",
            "referenceSolutionPython": "print('ok')",
        }
        validation = {"status": "passed", "checks": [], "findings": [], "summary": "ok", "score": 1.0}
        checked = repair._with_instruction_fidelity_validation(payload, draft, validation)
        self.assertEqual(checked["status"], "needs-review")
        self.assertTrue(any(item.get("code") == "instruction-exact-snippets" for item in checked.get("findings", [])))


if __name__ == "__main__":
    unittest.main()
