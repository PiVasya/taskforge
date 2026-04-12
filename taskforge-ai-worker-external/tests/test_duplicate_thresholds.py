import sys
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import reviews  # type: ignore


def _payload():
    return {
        "draft": {"title": "Сумма массива", "description": "Найдите сумму элементов массива и выведите ответ"},
        "referenceAssignments": [
            {"id": "r1", "title": "Сумма массива", "descriptionSummary": "Найдите сумму элементов массива и выведите ответ"},
            {"id": "r2", "title": "Сумма элементов", "descriptionSummary": "Посчитайте сумму всех элементов массива и напечатайте её"},
        ],
        "anchorContext": {},
    }


def test_similarity_review_uses_cluster_size_thresholds():
    payload = _payload()
    job = {"targetEntityId": "d1"}
    with patch.object(reviews, "DUPLICATE_CLUSTERING", True), \
         patch.object(reviews, "DUPLICATE_CLUSTER_WARNING_SIZE", 2), \
         patch.object(reviews, "DUPLICATE_CLUSTER_FAIL_SIZE", 4):
        result = reviews.run_similarity_review(payload, job)
    names = {c["name"]: c["status"] for c in result["checks"]}
    assert names["similarity-cluster-signature"] in {"warning", "failed"}
    assert "duplicateClusterSummary" in result
