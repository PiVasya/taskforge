import importlib.util
import pathlib
import unittest

module_path = pathlib.Path(__file__).resolve().parents[1] / "repair_disconnected.py"
spec = importlib.util.spec_from_file_location("repair_disconnected", module_path)
repair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair)


class GraphRepairTests(unittest.TestCase):
    def setUp(self):
        self.export = {"format": "taskforge-task-graph", "schemaVersion": 5,
            "courses": [{"key": "c", "id": "22222222-2222-4222-8222-222222222222", "title": "C"}],
            "tasks": [{"key": "a", "id": "33333333-3333-4333-8333-333333333333", "title": "A", "testCases": [{"input": "SECRET"}]}],
            "connections": [{"from": "$course", "to": "c"}]}

    def test_only_links_existing_ids_and_does_not_export_test_content(self):
        result = repair.build_repair(self.export, [("a", "c")])
        self.assertEqual(result["tasks"], [{"key": "a", "id": "33333333-3333-4333-8333-333333333333"}])
        self.assertEqual(result["connections"], [{"from": "$course", "to": "c"}, {"from": "c", "to": "a"}])
        self.assertNotIn("SECRET", str(result))

    def test_missing_reference_must_fail(self):
        with self.assertRaises(ValueError):
            repair.build_repair(self.export, [("unknown", "c")])
        with self.assertRaises(ValueError):
            repair.build_repair(self.export, [("a", "unknown")])

    def test_existing_link_is_noop(self):
        self.export["connections"].append({"from": "c", "to": "a"})
        with self.assertRaises(ValueError):
            repair.build_repair(self.export, [("a", "c")])


if __name__ == "__main__":
    unittest.main()
