from __future__ import annotations

from typing import Any, Dict, List


def _list(value: Any) -> List[Any]:
    return value if isinstance(value, list) else []


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
