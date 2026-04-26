from __future__ import annotations

from typing import Dict, List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from scenarios.base import Scenario


class CourseAnalysisScenario(Scenario):
    definition = ScenarioDefinition(
        id="course_analysis",
        name="Анализ курса",
        family="analysis",
        aliases=["анализ курса", "посмотри курс", "разбери курс", "что в курсе"],
        anti_aliases=["создай сразу"],
        default_count=0,
        default_mode="report",
        required_context=["course_digest", "recent_assignments"],
        pipeline=[
            "load_course",
            "build_course_digest",
            "extract_style_profile",
            "build_concept_map",
            "build_difficulty_curve",
            "return_course_report",
        ],
        output_type="course_analysis_report",
        can_run_directly=True,
        needs_course=True,
    )

    def run(
        self,
        context: AgentContextSnapshot,
        route: Optional[ScenarioRoute],
        previous_results: List[ScenarioResult],
    ) -> ScenarioResult:
        digest = context.course_digest or {"assignmentCount": 0, "assignments": []}
        assignments = digest.get("assignments") or []
        style = context.style_profile or {}
        concept_map = context.concept_map or {"concepts": []}
        difficulty_curve = [
            {
                "assignmentId": item.get("id"),
                "title": item.get("title"),
                "difficulty": item.get("difficulty", 1),
                "role": self._role_for(item),
            }
            for item in assignments
        ]
        observations = self._build_observations(digest, style, concept_map)
        warnings = []
        if not assignments:
            warnings.append("Курс/задания не пришли в payload, отчёт построен как пустой snapshot.")
        summary = self._summary(context, digest, observations)
        data = {
            "type": "course_analysis_report",
            "title": "Анализ курса",
            "courseId": context.course_id,
            "courseTitle": context.course_title,
            "summary": summary,
            "styleProfile": style,
            "conceptMap": concept_map.get("concepts", []),
            "difficultyCurve": difficulty_curve,
            "observations": observations,
            "warnings": warnings,
        }
        return ScenarioResult(
            type="course_analysis_report",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=82 if assignments else 55,
            warnings=warnings,
            validation={"pipeline": self.definition.pipeline},
        )

    @staticmethod
    def _role_for(item: Dict[str, object]) -> str:
        concepts = item.get("concepts") if isinstance(item.get("concepts"), list) else []
        difficulty = int(item.get("difficulty") or 1)
        if difficulty <= 1 and len(concepts) <= 1:
            return "first-touch"
        if difficulty >= 4:
            return "challenge"
        return "practice"

    @staticmethod
    def _summary(context: AgentContextSnapshot, digest: Dict[str, object], observations: List[str]) -> str:
        count = digest.get("assignmentCount") or 0
        title = context.course_title or "курс"
        if not count:
            return f"Собрал каркас анализа для курса «{title}», но в payload нет списка заданий. Нужен свежий course snapshot для точного аудита."
        return f"Проанализировал «{title}»: {count} заданий, стиль и карта понятий собраны. Главные наблюдения: {observations[0] if observations else 'критичных сигналов пока нет'}."

    @staticmethod
    def _build_observations(digest: Dict[str, object], style: Dict[str, object], concept_map: Dict[str, object]) -> List[str]:
        observations: List[str] = []
        concepts = [x.get("concept") for x in concept_map.get("concepts", []) if isinstance(x, dict)]
        if "if" in concepts and "comparison" not in concepts:
            observations.append("Перед условным ветвлением не видно отдельной тренировки сравнения.")
        if style.get("avgDescriptionLength", 0) and style.get("avgDescriptionLength", 0) < 160:
            observations.append("Условия выглядят короткими; для слабых мест лучше добавлять микро-шаги.")
        assignments = digest.get("assignments") or []
        for prev, cur in zip(assignments, assignments[1:]):
            if int(cur.get("difficulty") or 1) - int(prev.get("difficulty") or 1) >= 2:
                observations.append(f"Есть скачок сложности между «{prev.get('title')}» и «{cur.get('title')}».")
                break
        if not observations:
            observations.append("Курс можно улучшать через точечные мостики: 2-5 маленьких задач перед новыми понятиями.")
        return observations
