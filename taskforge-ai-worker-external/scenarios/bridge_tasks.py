from __future__ import annotations

from typing import List, Optional

from agent_core.context_pack import build_ai_context
from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient, compact_json
from scenarios.base import Scenario, llm_failed_result, previous_artifact, target_concept
from scenarios.llm_common import BASE_SYSTEM, as_dict, as_list, as_str


class BridgeTasksScenario(Scenario):
    definition = ScenarioDefinition(
        id="bridge_tasks",
        name="Мостики между заданиями",
        family="generation",
        aliases=["мостик", "между заданиями", "перед темой", "после задания"],
        anti_aliases=[],
        default_count=4,
        default_mode="bridge-plan",
        required_context=["course_catalog", "course_contexts", "user_message"],
        pipeline=["llm_find_anchor", "llm_compare_skills", "llm_build_bridge_plan"],
        output_type="bridge_plan",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        gap = previous_artifact(previous_results, "gap_audit_report") or context.gap_map or {}
        concept = target_concept(route, "if")
        schema = {
            "type": "bridge_plan",
            "title": "string",
            "summary": "string",
            "selectedCourseId": "string|null",
            "selectedCourseTitle": "string|null",
            "afterAssignmentId": "string|null",
            "beforeAssignmentId": "string|null",
            "items": [{"index": 1, "concept": "string", "titleHint": "string", "taskCount": 1, "difficulty": 1, "reason": "string"}],
            "warnings": ["string"],
        }
        user = (
            f"Построй bridge-plan между текущим уровнем ученика и темой {concept}. "
            "Если есть точные assignmentId из контекста, используй их. Если нет, не выдумывай id.\n\n"
            f"gap_report:\n{compact_json(gap, 12000)}\n\n"
            f"Контекст TaskForge:\n{build_ai_context(context, max_chars=78000)}\n\n"
            f"Схема результата:\n{schema}"
        )
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=user, purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Bridge-plan не сгенерирован")

        data = as_dict(parsed)
        data["type"] = "bridge_plan"
        data.setdefault("title", f"Мостик к теме {concept}")
        data.setdefault("items", [])
        data.setdefault("warnings", [])
        summary = as_str(data.get("summary"), f"LLM подготовил bridge-plan из {len(as_list(data.get('items')))} пунктов.")
        return ScenarioResult(
            type="bridge_plan",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=82,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False, "noPersistence": "ok"},
        )
