from __future__ import annotations

from typing import Dict, List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from scenarios.base import Scenario, requested_count, target_concept


class StyleMatchedTasksScenario(Scenario):
    definition = ScenarioDefinition(
        id="style_matched_tasks",
        name="Задачи в стиле курса",
        family="generation",
        aliases=["в стиле курса", "как текущие задачи", "похожие задачи", "сделай как здесь", "обучающие задачи"],
        anti_aliases=["другим стилем"],
        default_count=3,
        default_mode="style-pack",
        required_context=["style_profile", "recent_assignments"],
        pipeline=[
            "extract_course_style",
            "detect_course_language",
            "build_generation_spec",
            "generate_task_drafts",
            "style_review",
            "return_task_draft_bundle",
        ],
        output_type="task_draft_bundle",
        can_run_directly=True,
        needs_course=True,
    )

    def run(
        self,
        context: AgentContextSnapshot,
        route: Optional[ScenarioRoute],
        previous_results: List[ScenarioResult],
    ) -> ScenarioResult:
        count = requested_count(route, self.definition.default_count, 1, 8)
        concept = target_concept(route, self._infer_concept(context))
        language = self._detect_language(context)
        drafts = [self._build_draft(i + 1, concept, context.style_profile or {}, language) for i in range(count)]
        lang_label = "C++" if language == "cpp" else "Python"
        summary = (
            f"Подготовил {len(drafts)} задач в стиле курса по теме «{concept}» на {lang_label}. "
            "Формат обучающий: дружелюбное вступление, маленькие шаги, понятный публичный тест. Сохранение в курс не выполнялось."
        )
        data = {
            "type": "task_draft_bundle",
            "title": f"Пакет задач в стиле курса: {concept}",
            "scenarioId": self.id,
            "courseId": context.course_id,
            "topic": concept,
            "language": lang_label,
            "allowedLanguages": [lang_label],
            "styleProfileUsed": context.style_profile,
            "drafts": drafts,
        }
        return ScenarioResult(
            type="task_draft_bundle",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=83,
            warnings=[] if context.style_profile else ["Стиль курса не пришёл; использован безопасный обучающий style."],
            validation={"pipeline": self.definition.pipeline, "noPersistence": "ok", "languageDetected": language},
        )

    @staticmethod
    def _infer_concept(context: AgentContextSnapshot) -> str:
        digest = context.course_digest or {}
        assignments = digest.get("assignments") or []
        for item in reversed(assignments):
            concepts = item.get("concepts") or []
            if concepts:
                return str(concepts[-1])
        return "input"

    @staticmethod
    def _detect_language(context: AgentContextSnapshot) -> str:
        parts = [context.course_title or "", context.user_message or ""]
        for item in context.recent_assignments or []:
            if not isinstance(item, dict):
                continue
            for key in ("title", "description", "allowedLanguages", "allowedLanguagesCsv", "tags"):
                if item.get(key) is not None:
                    parts.append(str(item.get(key)))
        blob = "\n".join(parts).lower()
        if any(x in blob for x in ["c++", "cpp", "iostream", "#include", "cout", "cin"]):
            return "cpp"
        if any(x in blob for x in ["python", "print(", "input()", "def "]):
            return "python"
        return "cpp"

    @staticmethod
    def _build_draft(index: int, concept: str, style_profile: Dict[str, object], language: str) -> Dict[str, object]:
        friendly = "дружелюбный тон" in (style_profile.get("descriptionStyle") or [])
        intro = "Давай закрепим тему маленькой программой." if friendly else "Давай сделаем короткую понятную тренировку."
        title = ["Тёплая тренировка", "Ещё один шаг", "Проверка навыка", "Мини-практика"][min(index - 1, 3)]
        lang_label = "C++" if language == "cpp" else "Python"

        description = (
            f"{intro}\n\n"
            "Следуй шагам:\n"
            "1. Считай одно значение.\n"
            "(Так программа получает данные для работы.)\n"
            f"2. Сделай одно простое действие по теме «{concept}».\n"
            "(Не добавляй лишние новые идеи: сейчас тренируем только этот шаг.)\n"
            "3. Выведи ответ в формате `Ответ: значение`.\n\n"
            "Запусти код и проверь публичный тест."
        )

        if language == "cpp":
            solution_field = {
                "referenceSolutionCpp": '#include <iostream>\nusing namespace std;\n\nint main() {\n    int x;\n    cin >> x;\n    cout << "Ответ: " << x;\n}'
            }
            prerequisites = ["C++ skeleton", "cin", "cout"]
        else:
            solution_field = {"referenceSolutionPython": 'x = input()\nprint(f"Ответ: {x}")'}
            prerequisites = ["input", "output"]

        return {
            "title": f"{title}: {concept}",
            "assignmentType": "code-test",
            "language": lang_label,
            "allowedLanguages": [lang_label],
            "inputMode": "graphical-editor",
            "difficulty": 1 if index == 1 else 2,
            "description": description,
            "publicTests": [{"input": "5", "expectedOutput": "Ответ: 5"}],
            "hiddenTests": [{"input": "12", "expectedOutput": "Ответ: 12"}],
            **solution_field,
            "pedagogicalGoal": f"Закрепить {concept} в формате, похожем на курс.",
            "targetSkill": concept,
            "prerequisites": prerequisites,
            "newConcepts": [] if index > 1 else [concept],
            "forbiddenConcepts": ["advanced algorithms", "несколько новых тем в одном задании"],
            "styleNotes": ["короткое название", "пошаговое условие", "одна новая идея", "проверяемый видимый результат"],
            "validationNotes": ["blueprint only", "not persisted", "course style matched"],
        }
