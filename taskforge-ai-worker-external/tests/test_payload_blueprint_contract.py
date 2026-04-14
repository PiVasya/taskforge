import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(__file__))
sys.path.insert(0, ROOT)

import payload  # type: ignore


class PayloadBlueprintContractTests(unittest.TestCase):
    def test_sanitize_generation_result_keeps_approved_blueprint_and_normalizes_empty_input(self):
        request_payload = {
            "assignmentType": "code-test",
            "schemaVersion": "draft-v2",
            "prompt": "Сделай первое задание по шагам. Итог: cout << \"Hi\";",
            "sourceText": "cout << \"Hi\";",
            "teachingScript": "Напиши #include <iostream>, using namespace std, int main() { }, потом cout << \"Hi\";",
            "qualityGates": {"preferPublicTestsMoreThanHidden": True},
            "structuredContext": {
                "kind": "approved-chat-blueprint",
                "title": "Задание 1. Выводим «Hi»",
                "fullCondition": "Условие\n1. Напиши #include <iostream>\n2. Напиши using namespace std;\n3. Напиши int main() { }\n4. Внутри напиши cout << \"Hi\";",
                "conditionPreview": "cout << \"Hi\";",
                "mustKeep": ["#include <iostream>", "using namespace std;", "int main()", 'cout << "Hi";'],
                "placementAfterAssignmentId": "8ad8bfd9-de84-4a60-8e03-f4fa58cb0c74",
                "placementAfterTitle": "Первый маленький шаг",
                "placementReason": "Новая задача должна стоять сразу после первого маленького шага.",
                "publicTests": [],
            },
        }
        raw_result = {
            "draft": {
                "assignmentType": "code-test",
                "title": "__PENDING_TITLE__",
                "description": "Условие\nСчитай число и выведи его через scanf/printf",
                "publicTests": [{"input": "", "expectedOutput": "2\n"}],
                "hiddenTests": [{"input": "", "expectedOutput": "2\n"}],
                "requiredCalls": ["scanf"],
                "forbiddenCalls": ["cout"],
            }
        }

        sanitized = payload.sanitize_result_payload("assignment_generate_from_text", request_payload, raw_result)
        draft = sanitized["draft"]

        self.assertEqual(draft["title"], "Задание 1. Выводим «Hi»")
        self.assertIn('cout << "Hi";', draft["description"])
        self.assertEqual(draft["publicTests"][0]["input"], payload.NO_INPUT_SENTINEL)
        self.assertEqual(draft["publicTests"][0]["expectedOutput"], "Hi\n")
        self.assertTrue(draft["hiddenTests"][0]["input"])
        self.assertIn("print('Hi')", draft["referenceSolutionPython"])
        self.assertIn("cout", [x.lower() for x in draft["requiredCalls"]])


if __name__ == "__main__":
    unittest.main()
