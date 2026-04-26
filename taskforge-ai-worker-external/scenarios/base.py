from __future__ import annotations

from typing import Any, Dict, List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute


class Scenario:
    definition: ScenarioDefinition

    @property
    def id(self) -> str:
        return self.definition.id

    def can_run(self, context: AgentContextSnapshot) -> bool:
        # No scenario is allowed to fake success because a course is missing.
        # If context is weak, the real LLM must say so in the result.
        return True

    def run(
        self,
        context: AgentContextSnapshot,
        route: Optional[ScenarioRoute],
        previous_results: List[ScenarioResult],
    ) -> ScenarioResult:
        raise NotImplementedError


def requested_count(route: Optional[ScenarioRoute], default: int, minimum: int = 1, maximum: int = 12) -> int:
    value = route.requested_count if route and route.requested_count else default
    return max(minimum, min(maximum, int(value)))


def target_concept(route: Optional[ScenarioRoute], default: str = "if") -> str:
    if route and route.target_concept:
        return route.target_concept
    return default


def previous_artifact(previous_results: List[ScenarioResult], result_type: str) -> Optional[Dict[str, Any]]:
    for result in reversed(previous_results):
        if result.type == result_type:
            return result.data
    return None


def llm_failed_result(scenario_id: str, error: Exception | str, *, title: str = "LLM generation failed") -> ScenarioResult:
    message = (
        "Реальный LLM-вызов не завершился, поэтому я не подставляю шаблон и не выдаю фейковый результат. "
        f"Причина: {error}"
    )
    return ScenarioResult(
        type="llm_generation_failed",
        scenario_id=scenario_id,
        summary=message,
        data={"type": "llm_generation_failed", "title": title, "message": message},
        confidence=0,
        warnings=["real LLM call failed; template generation is disabled"],
        validation={"llmUsed": False, "templateUsed": False, "noFakeGeneration": "ok"},
    )

