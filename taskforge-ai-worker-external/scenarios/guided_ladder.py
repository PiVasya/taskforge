from __future__ import annotations

from typing import List, Optional

from agent_core.context_pack import build_ai_context
from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient, compact_json
from scenarios.base import Scenario, llm_failed_result, previous_artifact, requested_count, target_concept
from scenarios.llm_common import BASE_SYSTEM, TASKFORGE_TRAINING_TASK_STYLE, as_dict, as_list, as_str


class GuidedLadderScenario(Scenario):
    definition = ScenarioDefinition(
        id="guided_ladder",
        name="Задачки-лесенка",
        family="generation",
        aliases=["лесенка", "пошагово", "с нуля", "маленькие задачки", "как на скрине", "обучалка"],
        anti_aliases=["одну сложную", "без разжёвывания"],
        default_count=5,
        default_mode="guided-onboarding-ladder",
        required_context=["course_catalog", "course_contexts", "user_message"],
        pipeline=["llm_plan_ladder", "llm_generate_task_blueprints", "validate_ladder_progression"],
        output_type="task_ladder_blueprint",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        gap_report = previous_artifact(previous_results, "gap_audit_report") or context.gap_map or {}
        concept = target_concept(route, "if")
        count = requested_count(route, self.definition.default_count, 3, 8)
        schema = {
            "type": "task_ladder_blueprint",
            "title": "string",
            "summary": "string",
            "targetConcept": concept,
            "selectedCourseId": "string|null",
            "selectedCourseTitle": "string|null",
            "placement": {"afterAssignmentId": "string|null", "beforeAssignmentId": "string|null", "reason": "string"},
            "stylePreset": "friendly_step_by_step_training",
            "tasks": [{
                "index": 1,
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
                "microGoal": "string",
                "prerequisites": ["string"],
                "newConcepts": ["string"],
                "forbiddenConcepts": ["string"],
                "styleNotes": ["string"],
                "validationNotes": ["llm generated", "not persisted"]
            }],
            "warnings": ["string"],
        }
        user = (
            f"Собери обучающую лесенку из {count} маленьких задач. Целевая тема: {concept}.\n"
            "Главная цель — не просто придумать условия, а сделать обучающие задачи в стиле первых заданий курса: ученик должен видеть, какие строки писать в код и зачем.\n"
            "Если пользователь просит 'перед 20 задачей', используй gap_report/currentDraftBlueprint/lastGapAudit для placement.\n"
            "Если в контексте или прошлом черновике выбран C++/Основы C++, строго сохраняй C++: language='cpp', allowedLanguages=['cpp'], referenceSolutionCpp не null, referenceSolutionPython=null.\n"
            "Если тема if/условия, задача, где вводится if, обязана иметь if в referenceSolutionCpp. Первые шаги могут тренировать сравнения без if только если это явно подготовка перед if.\n"
            "Нельзя возвращать шаблонные одинаковые задачи, нельзя переключаться на Python при C++-курсе.\n\n"
            f"Эталон стиля обучающих задач:\n{TASKFORGE_TRAINING_TASK_STYLE}\n\n"
            f"gap_report:\n{compact_json(gap_report, 12000)}\n\n"
            f"Контекст TaskForge:\n{build_ai_context(context, max_chars=52000)}\n\n"
            f"Схема результата:\n{schema}"
        )
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=user, purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Лесенка не сгенерирована")

        data = as_dict(parsed)
        data["type"] = "task_ladder_blueprint"
        data.setdefault("title", f"Лесенка: {concept}")
        data.setdefault("targetConcept", concept)
        data.setdefault("tasks", [])
        data.setdefault("warnings", [])
        summary = as_str(data.get("summary"), f"LLM подготовил лесенку из {len(as_list(data.get('tasks')))} задач.")
        return ScenarioResult(
            type="task_ladder_blueprint",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=84,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False, "noPersistence": "ok"},
        )
