from __future__ import annotations

from typing import Any, Dict, List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from scenarios.base import Scenario


class DraftRevisionScenario(Scenario):
    definition = ScenarioDefinition(
        id="draft_revision",
        name="Правка черновиков",
        family="revision",
        aliases=["исправь", "переделай", "упрости", "сделай сложнее", "поправь"],
        anti_aliases=[],
        default_count=0,
        default_mode="revision-plan",
        required_context=["recent_drafts"],
        pipeline=["load_current_blueprint", "parse_feedback", "apply_targeted_revision", "return_revision_plan"],
        output_type="revision_plan",
        can_run_directly=True,
        needs_course=False,
    )

    def run(
        self,
        context: AgentContextSnapshot,
        route: Optional[ScenarioRoute],
        previous_results: List[ScenarioResult],
    ) -> ScenarioResult:
        current = self._current_blueprint(context)
        feedback = context.user_message
        changes = self._changes_from_feedback(feedback)
        summary = "Собрал план правки текущего blueprint. Если blueprint не был передан в payload, изменения описаны как target patch."
        data = {
            "type": "revision_plan",
            "title": "План правки черновиков",
            "feedback": feedback,
            "target": current.get("type") or "currentDraftBlueprint",
            "changes": changes,
            "revised": self._apply_light_patch(current, changes),
        }
        return ScenarioResult(
            type="revision_plan",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=72 if current else 55,
            warnings=[] if current else ["В payload нет currentDraftBlueprint; возвращён только план правки."],
            validation={"pipeline": self.definition.pipeline},
        )

    @staticmethod
    def _current_blueprint(context: AgentContextSnapshot) -> Dict[str, Any]:
        payload = context.raw_payload or {}
        memory = payload.get("memory") if isinstance(payload.get("memory"), dict) else {}
        for key in ["currentDraftBlueprint", "current_blueprint", "blueprint"]:
            value = payload.get(key) or memory.get(key)
            if isinstance(value, dict):
                return value
        return {}

    @staticmethod
    def _changes_from_feedback(feedback: str) -> List[Dict[str, Any]]:
        lowered = feedback.lower()
        changes: List[Dict[str, Any]] = []
        if any(word in lowered for word in ["упрост", "проще", "слишком сложно"]):
            changes.append({"field": "difficulty", "operation": "decrease", "scope": "all_tasks"})
            changes.append({"field": "description", "operation": "make_more_step_by_step", "scope": "all_tasks"})
        if any(word in lowered for word in ["сложнее", "мало", "слаб"]):
            changes.append({"field": "hiddenTests", "operation": "add_edge_cases", "scope": "all_tasks"})
        if any(word in lowered for word in ["дружелюб", "мягче", "теплее"]):
            changes.append({"field": "description", "operation": "make_tone_friendlier", "scope": "all_tasks"})
        if "убери if" in lowered or "без if" in lowered:
            changes.append({"field": "forbiddenConcepts", "operation": "add", "value": "if", "scope": "all_tasks"})
        if not changes:
            changes.append({"field": "description", "operation": "revise_according_to_feedback", "scope": "all_tasks"})
        return changes

    @staticmethod
    def _apply_light_patch(current: Dict[str, Any], changes: List[Dict[str, Any]]) -> Dict[str, Any]:
        if not current:
            return {"patchOnly": True, "changes": changes}
        revised = dict(current)
        revised.setdefault("revisionNotes", [])
        revised["revisionNotes"] = list(revised["revisionNotes"]) + changes
        return revised
