from __future__ import annotations

from typing import List, Optional

from agent_core.context_pack import build_ai_context
from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient, compact_json
from scenarios.base import Scenario, llm_failed_result, previous_artifact, target_concept
from scenarios.llm_common import BASE_SYSTEM, as_dict, as_list, as_str


class CourseGapAuditScenario(Scenario):
    definition = ScenarioDefinition(
        id="course_gap_audit",
        name="Поиск дыр в курсе/курсах",
        family="audit",
        aliases=["дырки", "пробелы", "скачки", "что пропущено", "найди слабые места"],
        anti_aliases=["не анализируй"],
        default_count=3,
        default_mode="gap-report",
        required_context=["course_catalog", "course_contexts"],
        pipeline=["llm_read_analysis", "llm_detect_gaps", "llm_rank_gaps", "return_gap_report"],
        output_type="gap_audit_report",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        analysis = previous_artifact(previous_results, "course_analysis_report") or context.last_course_audit or {}
        concept = target_concept(route, "if")
        schema = {
            "type": "gap_audit_report",
            "title": "string",
            "summary": "string",
            "targetConcept": concept,
            "findings": [{
                "id": "string",
                "kind": "missing_prerequisite|difficulty_jump|undertrained_concept|overcompressed_topic|style_break|missing_bridge|missing_context",
                "courseId": "string|null",
                "courseTitle": "string|null",
                "concept": "string",
                "afterAssignmentId": "string|null",
                "afterAssignmentTitle": "string|null",
                "beforeAssignmentId": "string|null",
                "beforeAssignmentTitle": "string|null",
                "reason": "string",
                "severity": 1,
                "suggestedScenario": "guided_ladder|style_matched_tasks|bridge_tasks|course_analysis",
                "suggestedTaskCount": 0,
                "suggestedDifficulty": 1
            }],
            "warnings": ["string"],
        }
        user = (
            "Найди реальные педагогические пробелы и скачки сложности. "
            f"Целевая тема: {concept}. Сначала проверь focusAssignments, затем весь courseMap/courseOutline. Если focusAssignments содержит задания с conceptHints по целевой теме, считай, что тема в курсе есть, и анализируй реальные переходы до/после этих заданий. Только если ни focusAssignments, ни courseMap/courseMap/courseOutline не содержат целевой темы, возвращай finding kind=missing_context. Не делай вывод, что курс обрывается на первых заданиях, если courseMap/courseOutline длиннее.\n\n"
            "Предыдущий анализ, если есть:\n"
            f"{compact_json(analysis, 12000)}\n\n"
            "Контекст TaskForge:\n"
            f"{build_ai_context(context, max_chars=78000)}\n\n"
            f"Схема результата:\n{schema}"
        )
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=user, purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Аудит дыр не выполнен")

        data = as_dict(parsed)
        data["type"] = "gap_audit_report"
        data.setdefault("title", "Поиск дыр в курсе")
        data.setdefault("targetConcept", concept)
        data.setdefault("findings", [])
        data.setdefault("warnings", [])
        summary = as_str(data.get("summary"), f"LLM выполнил аудит переходов к теме {concept}.")
        return ScenarioResult(
            type="gap_audit_report",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=86,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False},
        )
