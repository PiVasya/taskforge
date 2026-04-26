from __future__ import annotations

import re
from typing import Any, Dict, List

from agent_core.contracts import AgentContextSnapshot, ScenarioResult


def _solution_text(item: Dict[str, Any]) -> str:
    return str(item.get("referenceSolutionCpp") or item.get("referenceSolutionPython") or "")


def _has_if(solution: str) -> bool:
    return bool(re.search(r"\bif\s*\(", solution) or re.search(r"\bif\s+", solution))


def _fail_result(original: ScenarioResult, message: str, warnings: List[str], validation: Dict[str, Any]) -> ScenarioResult:
    validation["llmUsed"] = bool(original.validation.get("llmUsed"))
    validation["templateUsed"] = False
    validation["noFakeGeneration"] = "ok"
    return ScenarioResult(
        type="llm_generation_failed",
        scenario_id=original.scenario_id,
        summary=message,
        data={"type": "llm_generation_failed", "title": "LLM result rejected by validator", "message": message, "originalType": original.type},
        confidence=0,
        warnings=list(dict.fromkeys(warnings + [message])),
        validation=validation,
    )


class ResultValidator:
    def validate(self, result: ScenarioResult, context: AgentContextSnapshot) -> ScenarioResult:
        warnings = list(result.warnings)
        validation: Dict[str, Any] = dict(result.validation)
        validation.setdefault("templateUsed", False)

        if result.type == "llm_generation_failed":
            validation.setdefault("llmUsed", False)
            validation.setdefault("noFakeGeneration", "ok")
            result.warnings = list(dict.fromkeys(warnings))
            result.validation = validation
            return result

        if not validation.get("llmUsed") and result.scenario_id != "internal":
            return _fail_result(
                result,
                "Сценарий вернул результат без признака реального LLM-вызова. Такой ответ заблокирован, чтобы не показывать заглушку.",
                warnings,
                validation,
            )

        if result.type == "course_analysis_report":
            if not result.data.get("summary"):
                warnings.append("course_analysis_report.summary is empty")
            validation.setdefault("courseCatalogCount", len(context.course_catalog))
            validation.setdefault("courseContextCount", len(context.course_contexts))

        if result.type == "gap_audit_report":
            findings = result.data.get("findings") or []
            validation.setdefault("findingCount", len(findings))
            for index, finding in enumerate(findings):
                if not isinstance(finding, dict):
                    warnings.append(f"finding[{index}] is not an object")
                    continue
                if not finding.get("reason"):
                    warnings.append(f"finding[{index}] has no reason")

        if result.type == "task_ladder_blueprint":
            tasks = result.data.get("tasks") or []
            validation.setdefault("taskCount", len(tasks))
            if not tasks:
                return _fail_result(result, "LLM не вернул ни одной задачи для лесенки.", warnings, validation)
            target = str(result.data.get("targetConcept") or "").lower()
            invalid_if = 0
            if target == "if":
                for index, task in enumerate(tasks):
                    if not isinstance(task, dict):
                        warnings.append(f"task[{index}] is not an object")
                        invalid_if += 1
                        continue
                    new_concepts = [str(c).lower() for c in task.get("newConcepts", [])] if isinstance(task.get("newConcepts"), list) else []
                    says_if = any(c == "if" or "услов" in c for c in new_concepts) or "if" in str(task.get("targetSkill") or "").lower()
                    if says_if and not _has_if(_solution_text(task)):
                        warnings.append(f"task[{index}] claims to train if but reference solution has no if")
                        invalid_if += 1
                if invalid_if >= len(tasks):
                    return _fail_result(result, "Все задачи по if не прошли проверку: в эталонных решениях нет if.", warnings, validation)
            validation.setdefault("stepSize", "ok" if tasks else "missing")
            validation.setdefault("styleMatch", "llm-reviewed")

        if result.type == "task_draft_bundle":
            drafts = result.data.get("drafts") or []
            validation.setdefault("draftCount", len(drafts))
            if not drafts:
                return _fail_result(result, "LLM не вернул ни одного черновика задачи.", warnings, validation)
            topic = str(result.data.get("topic") or "").lower()
            invalid_if = 0
            for index, draft in enumerate(drafts):
                if not isinstance(draft, dict):
                    warnings.append(f"draft[{index}] is not an object")
                    invalid_if += 1 if topic == "if" else 0
                    continue
                if not draft.get("title") or not draft.get("description"):
                    warnings.append(f"draft[{index}] is missing title or description")
                if topic == "if" and not _has_if(_solution_text(draft)):
                    warnings.append(f"draft[{index}] claims to train if but reference solution has no if")
                    invalid_if += 1
            if topic == "if" and invalid_if >= len(drafts):
                return _fail_result(result, "Все черновики по if не прошли проверку: в эталонных решениях нет if.", warnings, validation)

        if result.type == "bridge_plan":
            items = result.data.get("items") or []
            validation.setdefault("itemCount", len(items))
            if not items:
                warnings.append("bridge_plan.items is empty")

        if result.type == "revision_plan":
            changes = result.data.get("changes") or []
            validation.setdefault("changeCount", len(changes))

        result.warnings = list(dict.fromkeys(warnings))
        result.validation = validation
        return result
