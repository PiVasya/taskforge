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




def _assignment_map_item(item: Dict[str, Any], index: int) -> Dict[str, Any]:
    return {
        "index": item.get("index", index),
        "sort": item.get("sort"),
        "id": item.get("id") or item.get("Id") or item.get("assignmentId"),
        "courseId": item.get("courseId") or item.get("CourseId"),
        "title": item.get("title") or item.get("Title") or item.get("name"),
        "type": item.get("type") or item.get("assignmentType"),
        "difficulty": item.get("difficulty"),
        "tags": item.get("tags"),
        "conceptHints": item.get("conceptHints") or item.get("concepts") or [],
        "contentSummary": item.get("contentSummary") or item.get("content_summary"),
    }


def build_course_map(assignments: List[Any], limit: int = 1500) -> List[Dict[str, Any]]:
    result: List[Dict[str, Any]] = []
    for index, item in enumerate(assignments[: max(0, limit)] if isinstance(assignments, list) else []):
        if isinstance(item, dict):
            result.append(_assignment_map_item(item, index))
    return result

def build_ai_context(context: AgentContextSnapshot, *, max_chars: int = 62000) -> str:
    """Build a compact, honest context packet for the LLM.

    Important: course catalog and selected/matched courses are placed BEFORE large
    assignment digests. This prevents the model from missing the course title when
    the digest is long and the JSON has to be trimmed.
    """
    matched_courses = context.raw_payload.get("matchedCourses") or context.raw_payload.get("matched_courses") or []
    selected_course = context.raw_payload.get("course") if isinstance(context.raw_payload.get("course"), dict) else None
    focus_assignments = context.raw_payload.get("focusAssignments") or context.raw_payload.get("targetAssignments") or []
    if not isinstance(focus_assignments, list):
        focus_assignments = []
    file_contexts = context.raw_payload.get("fileContexts") or context.raw_payload.get("attachments") or []
    if not isinstance(file_contexts, list):
        file_contexts = []
    editable_assignments = context.raw_payload.get("editableAssignments") or context.raw_payload.get("editable_assignments") or []
    if not isinstance(editable_assignments, list):
        editable_assignments = []
    course_outline = context.raw_payload.get("courseOutline") or context.raw_payload.get("course_outline") or context.recent_assignments or []
    if not isinstance(course_outline, list):
        course_outline = []
    target_concepts = context.raw_payload.get("targetConcepts") or context.raw_payload.get("target_concepts") or []
    memory = context.raw_payload.get("memory") if isinstance(context.raw_payload.get("memory"), dict) else {}
    current_draft = memory.get("currentDraftBlueprint") if isinstance(memory.get("currentDraftBlueprint"), dict) else None
    last_gap_audit = memory.get("lastGapAudit") if isinstance(memory.get("lastGapAudit"), dict) else None

    course_map = build_course_map(course_outline, limit=1500)

    payload: Dict[str, Any] = {
        "userMessage": context.user_message,
        "selectedCourseId": context.course_id,
        "selectedCourseTitle": context.course_title,
        "selectedCourse": selected_course,
        "targetConcepts": target_concepts if isinstance(target_concepts, list) else [],
        "courseMap": course_map,
        "courseMapPolicy": "courseMap is the compact full selected-course index and must be scanned before any absence claim. It is placed before verbose details so late modules survive context trimming.",
        "fileContexts": trim_list(file_contexts, 20),
        "fileContextPolicy": "User-uploaded files are first-class context. If textPreview is present, use it as source material and cite the fileName in your reasoning/summary. If extractStatus is binary_or_unsupported, acknowledge that only metadata is available.",
        "editableAssignments": trim_list(editable_assignments, 220),
        "editableAssignmentsPolicy": "For course editing scenarios this is the editable full snapshot: ids, order, difficulty, rating, descriptions, code tests, test questions and math blocks. Use ids exactly when returning update patches.",
        "courseOutline": trim_list(course_outline, 1000),
        "courseOutlinePolicy": "courseOutline is the full selected-course map. Do not conclude a topic is absent from focusAssignments only; scan courseMap/courseOutline/courseDigest first.",
        "focusAssignments": trim_list(focus_assignments, 90),
        "courseDigest": context.course_digest,
        "matchedCourses": trim_list(matched_courses if isinstance(matched_courses, list) else [], 12),
        "courseCatalog": trim_list(context.course_catalog, 200),
        "courseContexts": _trim_assignments_in_contexts(
            context.course_contexts,
            selected_course_id=context.course_id,
            per_course_limit=160 if context.course_id else 32,
            total_limit=220 if context.course_id else 120,
        ),
        "chatSummary": context.chat_summary,
        "hardRules": context.hard_rules,
        "styleProfile": context.style_profile,
        "conceptMap": context.concept_map,
        "recentMessages": trim_list(context.raw_payload.get("recentMessages") or [], 24),
        "recentDrafts": trim_list(context.recent_drafts, 10),
        "currentDraftBlueprint": current_draft,
        "lastGapAudit": last_gap_audit,
        "lastCourseAudit": context.last_course_audit,
        "lastBridgePlan": context.last_bridge_plan,
    }
    return compact_json(payload, max_chars=max_chars)
