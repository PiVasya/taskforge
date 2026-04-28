from __future__ import annotations

from typing import List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient
from scenarios.base import Scenario, llm_failed_result
from scenarios.llm_common import BASE_SYSTEM, TASKFORGE_TRAINING_TASK_STYLE, as_dict, as_list, as_str, context_user_block
from agent_core.languages import language_solution_schema_fields


class DraftRevisionScenario(Scenario):
    definition = ScenarioDefinition(
        id="draft_revision",
        name="Правка черновиков",
        family="revision",
        aliases=["исправь", "переделай", "упрости", "сделай сложнее", "поправь", "эти же", "каждым шагом"],
        anti_aliases=[],
        default_count=0,
        default_mode="revision-plan",
        required_context=["recent_drafts", "memory"],
        pipeline=["llm_load_blueprint", "llm_parse_feedback", "llm_revise"],
        output_type="revision_plan|task_ladder_blueprint|task_draft_bundle",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        schema = {
            "type": "task_ladder_blueprint|task_draft_bundle|revision_plan",
            "title": "string",
            "summary": "string",
            "targetConcept": "string|null",
            "selectedCourseId": "string|null",
            "selectedCourseTitle": "string|null",
            "placement": {"afterAssignmentId": "string|null", "beforeAssignmentId": "string|null", "reason": "string"},
            "stylePreset": "friendly_step_by_step_training",
            "tasks": [{
                "index": 1,
                "title": "string",
                "assignmentType": "code-test|test|math",
                "language": "string",
                "allowedLanguages": ["string"],
                "inputMode": "graphical-editor|code-editor|text",
                "difficulty": 1,
                "description": "string with step-by-step instructions",
                "publicTests": [{"input": "string", "expectedOutput": "string"}],
                "hiddenTests": [{"input": "string", "expectedOutput": "string"}],
                **language_solution_schema_fields(),
                "testSpec": {"settings": {}, "questions": [{"type": "single-choice|multi-choice|fill|text", "prompt": "string", "options": [{"key": "a", "text": "string"}], "correctOptionKeys": ["a"], "acceptedAnswers": ["string"]}]},
                "mathSpec": {"settings": {}, "blocks": [{"kind": "info|number|expression|set|single-choice|multi-choice|order|match", "prompt": "string", "acceptedAnswers": ["string"], "options": [{"key": "a", "text": "string"}], "correctOptionKeys": ["a"]}]},
                "pedagogicalGoal": "string",
                "targetSkill": "string",
                "microGoal": "string",
                "prerequisites": ["string"],
                "newConcepts": ["string"],
                "forbiddenConcepts": ["string"],
                "styleNotes": ["string"],
                "validationNotes": ["llm generated", "not persisted"]
            }],
            "drafts": [],
            "changes": [{"field": "string", "operation": "string", "scope": "string", "reason": "string"}],
            "warnings": ["string"],
        }
        task = f"""
Исправь последний blueprint/черновик с учётом сообщения пользователя.
Если пользователь говорит «эти же задачи», «каждым шагом», «лесенка лесенка», «как задача 1 и 1.1» — это не новая генерация с нуля, а правка текущего task_ladder_blueprint.

Критически важно:
- Сохрани тему, количество задач, placement и язык из currentDraftBlueprint, если они есть.
- Не переключай язык: сохраняй язык текущего черновика/курса.
- Для code-test верни эталонное решение в поле выбранного языка: cpp/referenceSolutionCpp, csharp/referenceSolutionCsharp, python/referenceSolutionPython, javascript/referenceSolutionJavascript, pascal/referenceSolutionPascal, java/referenceSolutionJava. Для test/math сохраняй testSpec/mathSpec и не требуй кодовое решение.
- Если правишь лесенку, лучше верни сразу исправленный объект type='task_ladder_blueprint', а не сухой revision_plan.
- Описания задач должны стать настоящими обучалками: с фразой «Следуй шагам:», нумерованными шагами и пояснениями в скобках, как в задачах 1 и 1.1.
- Не сохраняй в курс. Не используй шаблонные изменения. Не генерируй image/image-test: задачи на картинки выключены в AI-пайплайне.

Эталон стиля:
{TASKFORGE_TRAINING_TASK_STYLE}
"""
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=context_user_block(context, task, schema, max_chars=78000), purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Правка не выполнена")

        data = as_dict(parsed)
        result_type = as_str(data.get("type"), "revision_plan")
        if result_type not in {"task_ladder_blueprint", "task_draft_bundle", "revision_plan"}:
            result_type = "revision_plan"
            data["type"] = result_type

        data.setdefault("title", "Исправленный черновик")
        data.setdefault("warnings", [])
        if result_type == "task_ladder_blueprint":
            data.setdefault("tasks", [])
            summary = as_str(data.get("summary"), f"LLM исправил лесенку из {len(as_list(data.get('tasks')))} задач.")
        elif result_type == "task_draft_bundle":
            data.setdefault("drafts", [])
            summary = as_str(data.get("summary"), f"LLM исправил пакет из {len(as_list(data.get('drafts')))} задач.")
        else:
            data.setdefault("changes", [])
            summary = as_str(data.get("summary"), "LLM подготовил план правки.")

        return ScenarioResult(
            type=result_type,
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=80,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False},
        )
