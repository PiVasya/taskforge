import sys
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
from duplicate_clusters import cluster_duplicate_candidates  # type: ignore
class TestDuplicateClusters(unittest.TestCase):
    def test_clusters_group_similar_items(self):
        items = [
            {"id": "a", "title": "Сумма двух чисел", "description": "Прочитай два целых числа a и b. Вычисли их сумму a + b и выведи результат."},
            {"id": "b", "title": "Найди сумму a и b", "description": "Даны два целых числа a и b. Нужно найти сумму a + b и напечатать получившийся результат."},
            {"id": "c", "title": "Поменяй местами строки", "description": "Прочитай две строки и выведи их в обратном порядке."},
        ]
        clusters = cluster_duplicate_candidates(items, threshold=0.65, limit=6)
        assert clusters
        member_ids = {member.get("id") for member in clusters[0]["members"]}
        assert "a" in member_ids and "b" in member_ids and "c" not in member_ids
if __name__ == "__main__":
    unittest.main()
