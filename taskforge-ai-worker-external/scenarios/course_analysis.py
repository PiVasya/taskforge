from __future__ import annotations

from typing import List, Optional

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient
from scenarios.base import Scenario, llm_failed_result
from scenarios.llm_common import BASE_SYSTEM, as_dict, as_list, as_str, context_user_block


class CourseAnalysisScenario(Scenario):
    definition = ScenarioDefinition(
        id="course_analysis",
        name="Анализ курса/курсов",
        family="analysis",
        aliases=["анализ курса", "посмотри курс", "разбери курс", "что в курсе"],
        anti_aliases=["создай сразу"],
        default_count=0,
        default_mode="report",
        required_context=["course_catalog", "course_contexts"],
        pipeline=["llm_read_context", "llm_analyze_courses", "return_course_report"],
        output_type="course_analysis_report",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        schema = {
            "type": "course_analysis_report",
            "title": "string",
            "summary": "string",
            "selectedCourses": [{"courseId": "string|null", "title": "string", "reason": "string"}],
            "observations": ["string"],
            "styleProfile": {"titleStyle": ["string"], "descriptionStyle": ["string"], "taskShape": ["string"]},
            "conceptMap": [{"concept": "string", "introducedAt": "assignmentId|null", "reinforcedBy": ["assignmentId"]}],
            "difficultyCurve": [{"courseId": "string|null", "assignmentId": "string|null", "title": "string", "difficulty": 1, "role": "string"}],
            "warnings": ["string"],
            "nextActions": ["string"],
        }
        task = """
Проанализируй один или несколько курсов по запросу пользователя. Если selectedCourseId или selectedCourseTitle заполнены, анализируй именно этот курс и не пиши, что он отсутствует. Если selectedCourseId=null, выбери релевантные курсы из matchedCourses, courseCatalog и courseContexts сам.
Особенно внимательно смотри порядок заданий, стиль условий, карту понятий и места возможных скачков сложности.
Не создавай задания в этом сценарии.
"""
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=context_user_block(context, task, schema), purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Анализ курса не выполнен")

        data = as_dict(parsed)
        data["type"] = "course_analysis_report"
        data.setdefault("title", "Анализ курса")
        data.setdefault("selectedCourses", [])
        data.setdefault("observations", [])
        data.setdefault("warnings", [])
        summary = as_str(data.get("summary"), "LLM вернул анализ курса без краткого summary.")
        return ScenarioResult(
            type="course_analysis_report",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=86,
            warnings=[str(x) for x in as_list(data.get("warnings"))],
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False},
        )
