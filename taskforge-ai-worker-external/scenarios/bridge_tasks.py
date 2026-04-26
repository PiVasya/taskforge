from __future__ import annotations

from typing import Any, Dict, List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from scenarios.base import Scenario, previous_artifact, target_concept


class BridgeTasksScenario(Scenario):
    definition = ScenarioDefinition(
        id="bridge_tasks",
        name="Мостики между заданиями",
        family="generation",
        aliases=["мостик", "между заданиями", "перед темой", "после задания"],
        anti_aliases=[],
        default_count=4,
        default_mode="bridge-plan",
        required_context=["course_digest", "concept_map"],
        pipeline=["find_anchor_assignment", "compare_required_skills", "build_bridge_plan", "return_bridge_plan"],
        output_type="bridge_plan",
        can_run_directly=True,
        needs_course=True,
    )

    def run(
        self,
        context: AgentContextSnapshot,
        route: Optional[ScenarioRoute],
        previous_results: List[ScenarioResult],
    ) -> ScenarioResult:
        gap = previous_artifact(previous_results, "gap_audit_report") or context.gap_map or {}
        finding = (gap.get("findings") or [{}])[0] if isinstance(gap.get("findings"), list) else {}
        concept = str(finding.get("concept") or target_concept(route, "if"))
        items = self._items_for(concept)
        summary = f"Собрал bridge-plan: {len(items)} мостика для темы «{concept}»."
        data = {
            "type": "bridge_plan",
            "title": f"Мостик к теме {concept}",
            "courseId": context.course_id,
            "afterAssignmentId": finding.get("afterAssignmentId"),
            "beforeAssignmentId": finding.get("beforeAssignmentId"),
            "summary": summary,
            "items": items,
        }
        return ScenarioResult(
            type="bridge_plan",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=78,
            validation={"pipeline": self.definition.pipeline, "noPersistence": "ok"},
        )

    @staticmethod
    def _items_for(concept: str) -> List[Dict[str, Any]]:
        if "if" in concept or "услов" in concept:
            return [
                {"index": 1, "concept": "input", "titleHint": "Запомни число", "taskCount": 1, "difficulty": 1},
                {"index": 2, "concept": "comparison", "titleHint": "Больше или меньше", "taskCount": 1, "difficulty": 1},
                {"index": 3, "concept": "two outcomes", "titleHint": "Два ответа", "taskCount": 1, "difficulty": 1},
                {"index": 4, "concept": "first if", "titleHint": "Первое если", "taskCount": 1, "difficulty": 2},
            ]
        return [
            {"index": 1, "concept": concept, "titleHint": "Первый мягкий шаг", "taskCount": 1, "difficulty": 1},
            {"index": 2, "concept": concept, "titleHint": "Повторяем без скачка", "taskCount": 1, "difficulty": 1},
            {"index": 3, "concept": concept, "titleHint": "Маленький самостоятельный шаг", "taskCount": 1, "difficulty": 2},
        ]
