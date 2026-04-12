"""Cluster near-duplicate candidates using signature similarity."""
from __future__ import annotations
from typing import Any, Callable, Dict, Iterable, List
from similarity_signatures import similarity_signature_report
from text_utils import normalize_text

def _item_text(item: Dict[str, Any]) -> str:
    return "\n".join([
        normalize_text(item.get("title")),
        normalize_text(item.get("descriptionSummary") or item.get("description") or item.get("text")),
        normalize_text(item.get("microGoal")),
        normalize_text(item.get("targetSkill")),
    ]).strip()

def cluster_duplicate_candidates(items: Iterable[Dict[str, Any]], *, threshold: float = 0.72, limit: int = 8, text_getter: Callable[[Dict[str, Any]], str] | None = None) -> List[Dict[str, Any]]:
    getter = text_getter or _item_text
    materialized: List[Dict[str, Any]] = []
    for item in items:
        if not isinstance(item, dict):
            continue
        normalized = dict(item)
        normalized["_clusterText"] = getter(item)
        if not normalized["_clusterText"]:
            continue
        materialized.append(normalized)
        if len(materialized) >= max(2, limit):
            break
    if len(materialized) < 2:
        return []
    parent = list(range(len(materialized)))
    edges: List[Dict[str, Any]] = []
    def find(idx: int) -> int:
        while parent[idx] != idx:
            parent[idx] = parent[parent[idx]]
            idx = parent[idx]
        return idx
    def union(a: int, b: int) -> None:
        ra = find(a); rb = find(b)
        if ra != rb:
            parent[rb] = ra
    for i in range(len(materialized)):
        for j in range(i + 1, len(materialized)):
            report = similarity_signature_report(materialized[i]["_clusterText"], materialized[j]["_clusterText"])
            score = float(report.get("combined") or 0.0)
            if score < threshold:
                continue
            union(i, j)
            edges.append({"a": normalize_text(materialized[i].get("id")) or str(i), "b": normalize_text(materialized[j].get("id")) or str(j), "score": round(score, 4)})
    grouped: Dict[int, List[Dict[str, Any]]] = {}
    for idx, item in enumerate(materialized):
        grouped.setdefault(find(idx), []).append(item)
    clusters: List[Dict[str, Any]] = []
    for items_in_cluster in grouped.values():
        if len(items_in_cluster) < 2:
            continue
        ids = {normalize_text(item.get("id")) or str(pos) for pos, item in enumerate(items_in_cluster)}
        cluster_edges = [edge for edge in edges if edge["a"] in ids and edge["b"] in ids]
        max_score = max((float(edge["score"]) for edge in cluster_edges), default=threshold)
        avg_score = (sum(float(edge["score"]) for edge in cluster_edges) / len(cluster_edges)) if cluster_edges else max_score
        members = [{"id": item.get("id"), "title": normalize_text(item.get("title"))[:120], "source": normalize_text(item.get("source")) or None} for item in items_in_cluster]
        clusters.append({"size": len(members), "maxScore": round(max_score, 4), "avgScore": round(avg_score, 4), "members": members})
    clusters.sort(key=lambda cluster: (cluster["size"], cluster["maxScore"]), reverse=True)
    return clusters
