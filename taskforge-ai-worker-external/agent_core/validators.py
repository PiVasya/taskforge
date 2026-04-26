from __future__ import annotations

from typing import Any, Dict, List

from agent_core.contracts import AgentContextSnapshot, ScenarioResult


class ResultValidator:
    def validate(self, result: ScenarioResult, context: AgentContextSnapshot) -> ScenarioResult:
        warnings = list(result.warnings)
        validation: Dict[str, Any] = dict(result.validation)

        if result.type == "course_analysis_report":
            if not result.data.get("summary"):
                warnings.append("course_analysis_report.summary is empty")
            validation.setdefault("hasCourseContext", bool(context.course_digest and context.course_digest.get("assignmentCount")))

        if result.type == "gap_audit_report":
            findings = result.data.get("findings") or []
            validation.setdefault("findingCount", len(findings))
            for index, finding in enumerate(findings):
                if not finding.get("reason"):
                    warnings.append(f"finding[{index}] has no reason")

        if result.type == "task_ladder_blueprint":
            tasks = result.data.get("tasks") or []
            validation.setdefault("taskCount", len(tasks))
            if not tasks:
                warnings.append("task_ladder_blueprint.tasks is empty")
            early_tasks = tasks[:3]
            if any("if" in [c.lower() for c in task.get("newConcepts", [])] for task in early_tasks):
                warnings.append("if appears too early in the ladder")
            validation.setdefault("stepSize", "ok" if tasks else "missing")
            validation.setdefault("styleMatch", "ok")

        if result.type == "task_draft_bundle":
            drafts = result.data.get("drafts") or []
            validation.setdefault("draftCount", len(drafts))
            for index, draft in enumerate(drafts):
                if not draft.get("title") or not draft.get("description"):
                    warnings.append(f"draft[{index}] is missing title or description")

        result.warnings = list(dict.fromkeys(warnings))
        result.validation = validation
        return result
