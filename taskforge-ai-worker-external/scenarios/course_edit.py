from __future__ import annotations

from typing import List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient
from scenarios.base import Scenario, llm_failed_result
from scenarios.llm_common import BASE_SYSTEM, as_dict, as_list, as_str, context_user_block


class CourseEditScenario(Scenario):
    definition = ScenarioDefinition(
        id="course_edit",
        name="Редактирование существующих заданий курса",
        family="course-mutation",
        aliases=["редактируй курс", "подгони стиль", "расставь рейтинг", "переставь задания", "нормализуй задания"],
        anti_aliases=[],
        default_count=0,
        default_mode="apply-patch",
        required_context=["editableAssignments", "courseOutline"],
        pipeline=["llm_read_editable_course", "llm_plan_consistent_style", "llm_return_assignment_update_batch"],
        output_type="assignment_update_batch",
        can_run_directly=True,
        needs_course=True,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        schema = {
            "type": "assignment_update_batch",
            "title": "string",
            "summary": "string",
            "selectedCourseId": "string|null",
            "stylePolicy": {
                "titleStyle": "string",
                "descriptionStyle": "string",
                "testPolicy": "string",
                "ratingPolicy": "string",
            },
            "normalizeRatings": True,
            "ratingPolicy": "difficulty_based_10_20_30|custom",
            "order": ["assignment-id-in-final-order"],
            "assignments": [{
                "id": "existing assignment id; mandatory",
                "title": "new or unchanged title",
                "description": "full new statement if changed; can be TipTap JSON string or markdown/plain text",
                "type": "code-test|test|math|image-test",
                "difficulty": 1,
                "rating": 10,
                "sort": 0,
                "tags": "comma,separated,tags",
                "allowedLanguages": ["cpp"],
                "publicTests": [{"input": "string", "expectedOutput": "string"}],
                "hiddenTests": [{"input": "string", "expectedOutput": "string"}],
                "testSpec": {"settings": {}, "questions": [{"type": "single-choice|multi-choice|fill|text", "prompt": "string", "options": [{"key": "a", "text": "string"}], "correctOptionKeys": ["a"], "acceptedAnswers": ["string"]}]},
                "mathSpec": {"settings": {}, "blocks": [{"kind": "info|number|expression|set|single-choice|multi-choice|order|match", "prompt": "string", "score": 1, "acceptedAnswers": ["string"], "options": [{"key": "a", "text": "string"}], "correctOptionKeys": ["a"]}]},
                "changeReason": "string"
            }],
            "warnings": ["string"]
        }
        task = """
Пользователь просит изменить существующий курс/задания, а не просто дать совет.
Собери применимый patch: assignment_update_batch.

Правила:
1. Используй editableAssignments как источник истины. Там есть id, sort, type, difficulty, rating, description, testCases, testSpec, mathSpec.
2. Возвращай только существующие id. Никогда не придумывай id.
3. Если пользователь просит подогнать стиль, перепиши title/description у реально нужных заданий в единый стиль курса.
4. Если пользователь просит расставить рейтинг ровно, используй ratingPolicy difficulty_based_10_20_30: difficulty 1 => rating 10, difficulty 2 => 20, difficulty 3 => 30, если пользователь не попросил другую шкалу.
5. Если пользователь просит переставить задания, верни order — список существующих id в финальном порядке. Сохраняй педагогическую последовательность: теория/микро-навык перед практикой, простое перед сложным.
6. Поддерживай все типы: code-test, test, math, image-test. Для image-test можно менять title, description, tags, difficulty, rating, sort, но не выдумывай картинку/эталон.
7. Для code-test при изменении тестов возвращай publicTests/hiddenTests. Для test возвращай testSpec.questions, для math возвращай mathSpec.blocks.
8. Не превращай test/math/image-test в code-test без явной причины пользователя.
9. Сохраняй язык курса. Для C++ не переключайся на Python.
10. Если файловые материалы есть в fileContexts, используй их как материалы: добавляй термины/условия/примеры из textPreview, но не копируй бессмысленно весь файл.
11. Если правка слишком широкая, всё равно верни безопасный частичный patch и warnings.

Backend применит этот patch автоматически. Поэтому лучше меньше, но точнее: не меняй поля без причины.
"""
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=context_user_block(context, task, schema, max_chars=98000), purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Правка курса не выполнена")

        data = as_dict(parsed)
        data["type"] = "assignment_update_batch"
        data.setdefault("title", "Пакет правок существующих заданий")
        data.setdefault("warnings", [])
        data.setdefault("assignments", [])
        data.setdefault("order", [])
        if "normalizeRatings" not in data and "рейтинг" in (context.user_message or "").lower():
            data["normalizeRatings"] = True
            data.setdefault("ratingPolicy", "difficulty_based_10_20_30")
        summary = as_str(data.get("summary"), f"AI подготовил применимый patch для {len(as_list(data.get('assignments')))} заданий.")
        data.setdefault("summary", summary)
        return ScenarioResult(
            type="assignment_update_batch",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=86,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False, "willApplyToCourse": True},
        )
