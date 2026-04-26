from __future__ import annotations

from typing import Any, Dict, List

from agent_core.contracts import AgentContextSnapshot
from agent_core.llm_json import compact_json


def trim_list(values: List[Any], limit: int) -> List[Any]:
    return values[: max(0, limit)] if isinstance(values, list) else []


def _as_dict(value: Any) -> Dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _course_title(ctx: Dict[str, Any]) -> str:
    course = _as_dict(ctx.get("course"))
    return str(ctx.get("courseTitle") or course.get("title") or course.get("Title") or "")


def _course_id(ctx: Dict[str, Any]) -> str:
    course = _as_dict(ctx.get("course"))
    return str(ctx.get("courseId") or course.get("id") or course.get("Id") or "")


def _trim_assignments_in_contexts(contexts: List[Dict[str, Any]], *, selected_course_id: str | None, per_course_limit: int, total_limit: int) -> List[Dict[str, Any]]:
    result: List[Dict[str, Any]] = []
    used = 0
    ordered = sorted(
        [x for x in contexts if isinstance(x, dict)],
        key=lambda x: 0 if selected_course_id and _course_id(x) == str(selected_course_id) else 1,
    )
    for ctx in ordered:
        cloned = dict(ctx)
        assignments = cloned.get("assignments") if isinstance(cloned.get("assignments"), list) else []
        left = max(0, total_limit - used)
        take = min(per_course_limit, left)
        cloned["assignments"] = assignments[:take]
        used += len(cloned["assignments"])
        result.append(cloned)
        if used >= total_limit:
            break
    return result


def build_ai_context(context: AgentContextSnapshot, *, max_chars: int = 42000) -> str:
    """Build a compact, honest context packet for the LLM.

    Important: course catalog and selected/matched courses are placed BEFORE large
    assignment digests. This prevents the model from missing the course title when
    the digest is long and the JSON has to be trimmed.
    """
    matched_courses = context.raw_payload.get("matchedCourses") or context.raw_payload.get("matched_courses") or []
    selected_course = context.raw_payload.get("course") if isinstance(context.raw_payload.get("course"), dict) else None
    selected_assignments = context.recent_assignments if context.course_id else []

    payload: Dict[str, Any] = {
        "userMessage": context.user_message,
        "selectedCourseId": context.course_id,
        "selectedCourseTitle": context.course_title,
        "selectedCourse": selected_course,
        "matchedCourses": trim_list(matched_courses if isinstance(matched_courses, list) else [], 12),
        "courseCatalog": trim_list(context.course_catalog, 200),
        "courseContexts": _trim_assignments_in_contexts(
            context.course_contexts,
            selected_course_id=context.course_id,
            per_course_limit=90 if context.course_id else 35,
            total_limit=140 if context.course_id else 180,
        ),
        "selectedAssignments": trim_list(selected_assignments, 140),
        "chatSummary": context.chat_summary,
        "hardRules": context.hard_rules,
        "courseDigest": context.course_digest,
        "styleProfile": context.style_profile,
        "conceptMap": context.concept_map,
        "recentMessages": trim_list(context.raw_payload.get("recentMessages") or [], 24),
        "recentDrafts": trim_list(context.recent_drafts, 10),
        "lastCourseAudit": context.last_course_audit,
        "lastBridgePlan": context.last_bridge_plan,
    }
    return compact_json(payload, max_chars=max_chars)
