from __future__ import annotations

from typing import List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient
from scenarios.base import Scenario, llm_failed_result
from scenarios.llm_common import BASE_SYSTEM, as_dict, as_list, as_str, context_user_block


class FreeChatScenario(Scenario):
    definition = ScenarioDefinition(
        id="free_chat",
        name="Живой AI-чат",
        family="chat",
        aliases=["чат", "вопрос", "объясни"],
        anti_aliases=[],
        default_count=0,
        default_mode="chat",
        required_context=["user_message", "course_catalog"],
        pipeline=["llm_answer_with_context"],
        output_type="chat_answer",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        schema = {
            "type": "chat_answer",
            "title": "string",
            "summary": "string",
            "answer": "string",
            "usedCourses": [{"courseId": "string|null", "title": "string", "reason": "string"}],
            "warnings": ["string"],
        }
        task = """
Ответь как живой AI-ассистент TaskForge. Можно использовать любые курсы из courseCatalog/courseContexts.
Если пользователь просит действие, но оно требует отдельного сценария, объясни коротко и предложи следующий запрос.
Не делай вид, что выполнил генерацию/сохранение, если этого не было.
"""
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=context_user_block(context, task, schema), purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="AI-ответ не выполнен")

        data = as_dict(parsed)
        data["type"] = "chat_answer"
        data.setdefault("title", "AI-ответ")
        data.setdefault("warnings", [])
        summary = as_str(data.get("answer") or data.get("summary"), "LLM ответил, но не вернул текст ответа.")
        data.setdefault("summary", summary)
        return ScenarioResult(
            type="chat_answer",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=80,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False},
        )
