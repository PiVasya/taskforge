from __future__ import annotations

from typing import List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient
from scenarios.base import Scenario, llm_failed_result, requested_count, target_concept
from scenarios.llm_common import BASE_SYSTEM, as_dict, as_list, as_str, context_user_block


class StyleMatchedTasksScenario(Scenario):
    definition = ScenarioDefinition(
        id="style_matched_tasks",
        name="Задачи в стиле курса",
        family="generation",
        aliases=["в стиле курса", "как текущие задачи", "похожие задачи", "сделай как здесь"],
        anti_aliases=["другим стилем"],
        default_count=3,
        default_mode="style-pack",
        required_context=["course_catalog", "course_contexts", "user_message"],
        pipeline=["llm_understand_request", "llm_generate_task_drafts", "validate_no_persistence"],
        output_type="task_draft_bundle",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        count = requested_count(route, self.definition.default_count, 1, 8)
        topic = target_concept(route, "general")
        schema = {
            "type": "task_draft_bundle",
            "title": "string",
            "summary": "string",
            "topic": topic,
            "language": "C++|Python|JavaScript|C#|Pascal|Java|unknown",
            "selectedCourseId": "string|null",
            "selectedCourseTitle": "string|null",
            "drafts": [{
                "title": "string",
                "assignmentType": "code-test|text|math|image|other",
                "language": "string",
                "allowedLanguages": ["string"],
                "inputMode": "graphical-editor|code-editor|text",
                "difficulty": 1,
                "description": "string",
                "publicTests": [{"input": "string", "expectedOutput": "string"}],
                "hiddenTests": [{"input": "string", "expectedOutput": "string"}],
                "referenceSolutionCpp": "string|null",
                "referenceSolutionPython": "string|null",
                "pedagogicalGoal": "string",
                "targetSkill": "string",
                "prerequisites": ["string"],
                "newConcepts": ["string"],
                "forbiddenConcepts": ["string"],
                "styleNotes": ["string"],
                "validationNotes": ["llm generated", "not persisted"]
            }],
            "warnings": ["string"],
        }
        task = f"""
Создай {count} качественных черновиков задач по запросу пользователя. Если пользователь упоминает конкретный курс, выбери его из courseCatalog/courseContexts. Если курс не указан, работай как общий AI-ассистент и используй все доступные курсы как справочный стиль.
Тема/навык: {topic}.
Не сохраняй в курс. Не используй шаблон-заглушку. Каждая задача должна быть реально осмысленной и отличаться от остальных.
Если тема if/условия, эталонное решение обязано реально содержать if.
"""
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=context_user_block(context, task, schema), purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Генерация задач не выполнена")

        data = as_dict(parsed)
        data["type"] = "task_draft_bundle"
        data.setdefault("title", f"Пакет задач: {topic}")
        data.setdefault("topic", topic)
        data.setdefault("drafts", [])
        data.setdefault("warnings", [])
        summary = as_str(data.get("summary"), f"LLM подготовил {len(as_list(data.get('drafts')))} черновиков задач.")
        return ScenarioResult(
            type="task_draft_bundle",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=84,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False, "noPersistence": "ok"},
        )
