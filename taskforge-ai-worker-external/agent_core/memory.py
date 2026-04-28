from __future__ import annotations

from typing import Any, Dict, List, Optional


def _list(value: Any) -> List[Any]:
    return value if isinstance(value, list) else []


def _dict(value: Any) -> Dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _first_text(*values: Any) -> Optional[str]:
    for value in values:
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


def _result_course_hint(result_data: Dict[str, Any]) -> tuple[Optional[str], Optional[str]]:
    direct_id = _first_text(result_data.get("selectedCourseId"), result_data.get("courseId"), result_data.get("activeCourseId"))
    direct_title = _first_text(result_data.get("selectedCourseTitle"), result_data.get("courseTitle"), result_data.get("activeCourseTitle"))
    if direct_id:
        return direct_id, direct_title

    selected_courses = _list(result_data.get("selectedCourses"))
    if len(selected_courses) == 1:
        course = _dict(selected_courses[0])
        return _first_text(course.get("courseId"), course.get("id")), _first_text(course.get("title"), course.get("courseTitle"))

    used_courses = _list(result_data.get("usedCourses"))
    if len(used_courses) == 1:
        course = _dict(used_courses[0])
        return _first_text(course.get("courseId"), course.get("id")), _first_text(course.get("title"), course.get("courseTitle"))

    findings = [_dict(x) for x in _list(result_data.get("findings"))]
    finding_ids = [_first_text(x.get("courseId")) for x in findings]
    finding_ids = [x for x in finding_ids if x]
    if finding_ids and len(set(finding_ids)) == 1:
        title = _first_text(*[x.get("courseTitle") for x in findings])
        return finding_ids[0], title

    current = _dict(result_data.get("currentDraftBlueprint"))
    if current:
        return _result_course_hint(current)

    return None, direct_title


class MemoryStore:
    """Payload-backed memory adapter.

    The real DB-backed memory lives in TaskForge API later. For now the worker accepts
    whatever memory/checkpoints the backend sends in a job payload and returns a patch
    in ResultEnvelope.memory_patch.
    """

    def load(self, raw_memory: Dict[str, Any] | None) -> Dict[str, Any]:
        return raw_memory if isinstance(raw_memory, dict) else {}

    def extract_hard_rules(self, memory: Dict[str, Any], message_text: str) -> List[str]:
        rules = [str(x) for x in _list(memory.get("hardRules") or memory.get("hard_rules")) if str(x).strip()]
        lowered = message_text.lower()
        if "можно хардкод" in lowered or "разрешаю хардкод" in lowered:
            rules.append("Можно хардкодить сценарии для agent routing.")
        if "пока" in lowered and "сохран" in lowered:
            rules.append("Сохранение заданий в курс пока не требуется.")
        if "скрин" in lowered or "лесен" in lowered:
            rules.append("Для лесенок использовать preset ladder_screenshot_1.")
        return list(dict.fromkeys(rules))

    def build_patch(self, scenario_id: str, result_data: Dict[str, Any]) -> Dict[str, Any]:
        patch: Dict[str, Any] = {
            "latestIntentKind": scenario_id,
            "latestScenarioId": scenario_id,
        }

        course_id, course_title = _result_course_hint(result_data)
        if course_id:
            patch["activeCourseId"] = course_id
        if course_title:
            patch["activeCourseTitle"] = course_title

        result_type = result_data.get("type") if isinstance(result_data, dict) else None
        if result_type == "course_analysis_report":
            patch["lastCourseAnalysis"] = result_data
        elif result_type == "gap_audit_report":
            patch["lastGapAudit"] = result_data
        elif result_type in {"task_ladder_blueprint", "task_draft_bundle", "bridge_plan"}:
            patch["currentDraftBlueprint"] = result_data
        elif result_type == "revision_plan":
            patch["currentDraftBlueprint"] = result_data.get("revised") or result_data
        return patch
