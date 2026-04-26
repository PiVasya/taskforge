from __future__ import annotations

from typing import Any, Dict, List

from agent_core.contracts import AgentContextSnapshot
from agent_core.llm_json import compact_json


def trim_list(values: List[Any], limit: int) -> List[Any]:
    return values[: max(0, limit)] if isinstance(values, list) else []


def build_ai_context(context: AgentContextSnapshot, *, max_chars: int = 32000) -> str:
    """Build a compact, honest context packet for the LLM.

    This function does not generate answers. It only serializes data that came
    from TaskForge/backend/chat memory so the model can reason over it.
    """
    payload: Dict[str, Any] = {
        "userMessage": context.user_message,
        "selectedCourseId": context.course_id,
        "selectedCourseTitle": context.course_title,
        "chatSummary": context.chat_summary,
        "hardRules": context.hard_rules,
        "courseDigest": context.course_digest,
        "styleProfile": context.style_profile,
        "conceptMap": context.concept_map,
        "recentAssignments": trim_list(context.recent_assignments, 80),
        "courseCatalog": trim_list(context.course_catalog, 200),
        "courseContexts": trim_list(context.course_contexts, 24),
        "recentMessages": trim_list(context.raw_payload.get("recentMessages") or [], 24),
        "recentDrafts": trim_list(context.recent_drafts, 10),
        "lastCourseAudit": context.last_course_audit,
        "lastBridgePlan": context.last_bridge_plan,
    }
    return compact_json(payload, max_chars=max_chars)
