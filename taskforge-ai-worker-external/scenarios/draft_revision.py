from __future__ import annotations

from typing import List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient
from scenarios.base import Scenario, llm_failed_result
from scenarios.llm_common import BASE_SYSTEM, as_dict, as_list, as_str, context_user_block


class DraftRevisionScenario(Scenario):
    definition = ScenarioDefinition(
        id="draft_revision",
        name="Правка черновиков",
        family="revision",
        aliases=["исправь", "переделай", "упрости", "сделай сложнее", "поправь"],
        anti_aliases=[],
        default_count=0,
        default_mode="revision-plan",
        required_context=["recent_drafts", "memory"],
        pipeline=["llm_load_blueprint", "llm_parse_feedback", "llm_revise"],
        output_type="revision_plan",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        schema = {
            "type": "revision_plan",
            "title": "string",
            "summary": "string",
            "target": "string",
            "changes": [{"field": "string", "operation": "string", "scope": "string", "reason": "string"}],
            "revised": {},
            "warnings": ["string"],
        }
        task = """
Исправь или перепланируй последний blueprint/черновик с учётом сообщения пользователя.
Если в памяти нет черновика, честно верни warnings и предложи, что нужно прислать.
Не создавай шаблонные изменения; исправления должны следовать фидбеку пользователя.
"""
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=context_user_block(context, task, schema), purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Правка не выполнена")

        data = as_dict(parsed)
        data["type"] = "revision_plan"
        data.setdefault("title", "План правки")
        data.setdefault("changes", [])
        data.setdefault("warnings", [])
        summary = as_str(data.get("summary"), "LLM подготовил план правки.")
        return ScenarioResult(
            type="revision_plan",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=78,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False},
        )
