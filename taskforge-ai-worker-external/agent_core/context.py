from __future__ import annotations

import re
from typing import Any, Dict, List, Optional

from agent_core.contracts import AgentContextSnapshot, IncomingAgentMessage
from agent_core.memory import MemoryStore


CONCEPT_KEYWORDS = {
    "output": ["print", "вывод", "напечат", "экран", "cout"],
    "input": ["input", "ввод", "считы", "прочита", "ввести", "cin"],
    "variables": ["переменн", "variable", "присво", "хран"],
    "comparison": ["сравн", "больше", "меньше", "больш", "меньш", "равно", "порог"],
    "if": ["if", "если", "услов", "ветвлен", "else"],
    "loops": ["цикл", "for", "while", "повтор"],
    "arrays": ["массив", "список", "array", "list", "vector"],
    "functions": ["функц", "def", "метод"],
    "strings": ["строк", "символ", "string"],
}


def _first(*values: Any) -> Optional[Any]:
    for value in values:
        if value not in (None, ""):
            return value
    return None


def _as_list(value: Any) -> List[Any]:
    return value if isinstance(value, list) else []


def _extract_course_catalog(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    for key in ["courseCatalog", "course_catalog", "allCourses", "courses"]:
        value = payload.get(key)
        if isinstance(value, list):
            return [x for x in value if isinstance(x, dict)]
    return []


def _extract_course_contexts(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    for key in ["courseContexts", "course_contexts", "courseSnapshots"]:
        value = payload.get(key)
        if isinstance(value, list):
            return [x for x in value if isinstance(x, dict)]
    return []


def _extract_assignments_from_payload(payload: Dict[str, Any], selected_course: Optional[Dict[str, Any]]) -> List[Dict[str, Any]]:
    candidates = [
        payload.get("assignments"),
        payload.get("recentAssignments"),
        payload.get("recent_assignments"),
        (selected_course or {}).get("assignments") if isinstance(selected_course, dict) else None,
        (selected_course or {}).get("Assignments") if isinstance(selected_course, dict) else None,
    ]
    for candidate in candidates:
        if isinstance(candidate, list) and candidate:
            return [x for x in candidate if isinstance(x, dict)]

    flattened: List[Dict[str, Any]] = []
    for course_ctx in _extract_course_contexts(payload):
        course = course_ctx.get("course") if isinstance(course_ctx.get("course"), dict) else {}
        course_id = _first(course_ctx.get("courseId"), course.get("id"), course.get("Id"))
        course_title = _first(course_ctx.get("courseTitle"), course.get("title"), course.get("Title"))
        for item in _as_list(course_ctx.get("assignments")):
            if not isinstance(item, dict):
                continue
            cloned = dict(item)
            cloned.setdefault("courseId", course_id)
            cloned.setdefault("courseTitle", course_title)
            flattened.append(cloned)
    return flattened


def assignment_text(assignment: Dict[str, Any]) -> str:
    values = [
        assignment.get("courseTitle"), assignment.get("title"), assignment.get("Title"), assignment.get("name"),
        assignment.get("description"), assignment.get("Description"), assignment.get("text"),
        assignment.get("overview"), assignment.get("aiOverview"), assignment.get("type"), assignment.get("tags"),
    ]
    return "\n".join(str(v) for v in values if v).lower()


def assignment_id(assignment: Dict[str, Any], index: int) -> str:
    return str(_first(assignment.get("id"), assignment.get("Id"), assignment.get("assignmentId"), f"assignment-{index + 1}"))


def assignment_title(assignment: Dict[str, Any], index: int) -> str:
    return str(_first(assignment.get("title"), assignment.get("Title"), assignment.get("name"), f"Задание {index + 1}"))


def detect_concepts(text: str) -> List[str]:
    concepts: List[str] = []
    for concept, keywords in CONCEPT_KEYWORDS.items():
        if any(keyword in text for keyword in keywords):
            concepts.append(concept)
    return concepts


def estimate_difficulty(text: str, concepts: List[str]) -> int:
    score = 1
    score += max(0, len(concepts) - 1)
    if len(text) > 900:
        score += 1
    if any(word in text for word in ["слож", "алгоритм", "оптим", "рекурс", "граф"]):
        score += 2
    return max(1, min(5, score))


def build_course_digest(course_title: Optional[str], assignments: List[Dict[str, Any]], course_count: int = 0) -> Dict[str, Any]:
    items: List[Dict[str, Any]] = []
    concept_order: Dict[str, str] = {}
    for index, assignment in enumerate(assignments):
        text = assignment_text(assignment)
        concepts = detect_concepts(text)
        item_id = assignment_id(assignment, index)
        for concept in concepts:
            concept_order.setdefault(concept, item_id)
        items.append({
            "id": item_id,
            "courseId": _first(assignment.get("courseId"), assignment.get("CourseId")),
            "courseTitle": assignment.get("courseTitle"),
            "title": assignment_title(assignment, index),
            "index": index,
            "concepts": concepts,
            "difficulty": estimate_difficulty(text, concepts),
        })
    return {
        "courseTitle": course_title,
        "courseCount": course_count,
        "assignmentCount": len(assignments),
        "assignments": items,
        "conceptFirstSeen": concept_order,
        "freshness": "payload-derived" if assignments or course_count else "no-course-data",
    }


def build_style_profile(assignments: List[Dict[str, Any]]) -> Dict[str, Any]:
    titles = [assignment_title(a, i) for i, a in enumerate(assignments)]
    descriptions = [str(_first(a.get("description"), a.get("Description"), "")) for a in assignments]
    avg_title_len = round(sum(len(x) for x in titles) / max(1, len(titles)), 1)
    avg_desc_len = round(sum(len(x) for x in descriptions) / max(1, len(descriptions)), 1)
    friendly_markers = sum(1 for d in descriptions if re.search(r"ты|давай|сегодня|попроб", d, re.IGNORECASE))
    has_steps = sum(1 for d in descriptions if re.search(r"\b1[).]|шаг|следуй", d, re.IGNORECASE))
    return {
        "titleStyle": ["короткие названия" if avg_title_len <= 32 else "развёрнутые названия"],
        "descriptionStyle": [
            "дружелюбный тон" if friendly_markers else "нейтральный тон",
            "пошаговые инструкции" if has_steps else "условия без явных шагов",
        ],
        "taskShape": ["маленькая практическая задача", "видимый результат", "проверяемые примеры"],
        "avgTitleLength": avg_title_len,
        "avgDescriptionLength": avg_desc_len,
        "sourceAssignmentCount": len(assignments),
    }


def build_concept_map(digest: Dict[str, Any]) -> Dict[str, Any]:
    concepts: Dict[str, Dict[str, Any]] = {}
    for item in digest.get("assignments", []):
        for concept in item.get("concepts", []):
            entry = concepts.setdefault(concept, {"concept": concept, "introducedAt": item.get("id"), "reinforcedBy": []})
            if entry["introducedAt"] != item.get("id"):
                entry["reinforcedBy"].append(item.get("id"))
    return {"concepts": list(concepts.values()), "source": digest.get("freshness")}


class ContextSupervisor:
    def __init__(self, memory_store: Optional[MemoryStore] = None) -> None:
        self.memory_store = memory_store or MemoryStore()

    def build_snapshot(self, message: IncomingAgentMessage, allow_stale: bool = True) -> AgentContextSnapshot:
        memory = self.memory_store.load(message.current_memory)
        payload = message.payload or {}
        selected_course = message.selected_course or {}
        course_catalog = _extract_course_catalog(payload)
        course_contexts = _extract_course_contexts(payload)
        course_title = _first(
            selected_course.get("title"), selected_course.get("Title"), selected_course.get("name"), selected_course.get("Name")
        ) if isinstance(selected_course, dict) else None
        if not course_title and course_contexts:
            if message.course_id:
                for ctx in course_contexts:
                    course = ctx.get("course") if isinstance(ctx.get("course"), dict) else {}
                    if str(_first(ctx.get("courseId"), course.get("id"), course.get("Id"))) == str(message.course_id):
                        course_title = str(_first(ctx.get("courseTitle"), course.get("title"), course.get("Title"), "")) or None
                        break
            elif len(course_contexts) > 1:
                course_title = "несколько доступных курсов"
        assignments = _extract_assignments_from_payload(payload, message.selected_course)
        existing_digest = payload.get("courseDigest") or memory.get("courseDigest") or memory.get("lastCourseAnalysis")
        digest = existing_digest if isinstance(existing_digest, dict) and existing_digest.get("assignments") else build_course_digest(
            str(course_title) if course_title else None,
            assignments,
            course_count=len(course_catalog) or len(course_contexts),
        )
        style = payload.get("styleProfile") or memory.get("styleProfile")
        if not isinstance(style, dict):
            style = build_style_profile(assignments)
        concept_map = payload.get("conceptMap") or memory.get("conceptMap")
        if not isinstance(concept_map, dict):
            concept_map = build_concept_map(digest)
        return AgentContextSnapshot(
            course_id=message.course_id,
            course_title=str(course_title) if course_title else None,
            user_message=message.raw_text,
            chat_summary=str(memory.get("summary") or memory.get("chatSummary") or ""),
            active_goals=[str(x) for x in _as_list(memory.get("activeGoals") or memory.get("active_goals"))],
            hard_rules=self.memory_store.extract_hard_rules(memory, message.raw_text),
            course_digest=digest,
            style_profile=style,
            concept_map=concept_map,
            gap_map=payload.get("gapMap") if isinstance(payload.get("gapMap"), dict) else memory.get("lastGapAudit"),
            recent_assignments=assignments,
            landmark_assignments=assignments[:5] + assignments[-5:] if len(assignments) > 10 else assignments,
            recent_drafts=message.recent_drafts,
            last_course_audit=memory.get("lastCourseAnalysis") if isinstance(memory.get("lastCourseAnalysis"), dict) else None,
            last_bridge_plan=memory.get("lastBridgePlan") if isinstance(memory.get("lastBridgePlan"), dict) else None,
            course_catalog=course_catalog,
            course_contexts=course_contexts,
            raw_payload=payload,
        )
