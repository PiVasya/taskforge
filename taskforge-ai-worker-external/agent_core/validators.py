from __future__ import annotations

import re
from typing import Any, Dict, List

from agent_core.contracts import AgentContextSnapshot, ScenarioResult


def _solution_text(item: Dict[str, Any]) -> str:
    return str(item.get("referenceSolutionCpp") or item.get("referenceSolutionPython") or "")


def _has_if(solution: str) -> bool:
    return bool(re.search(r"\bif\s*\(", solution) or re.search(r"\bif\s+", solution))


def _expects_cpp(context: AgentContextSnapshot) -> bool:
    haystack = " ".join([
        str(context.user_message or ""),
        str(context.course_title or ""),
        str(context.raw_payload.get("selectedCourseTitle") or ""),
        str((context.raw_payload.get("course") or {}).get("title") if isinstance(context.raw_payload.get("course"), dict) else ""),
        str(context.raw_payload.get("memory") or ""),
    ]).lower()
    return any(x in haystack for x in ["c++", "с++", "cpp", "си++", "основы c", "основы с"] )


def _task_is_cpp(item: Dict[str, Any]) -> bool:
    assignment_type = str(item.get("assignmentType") or "code-test").lower().replace("_", "-")
    if assignment_type in {"test", "math"}:
        # Non-code task types belong to the same course but do not need a C++ reference solution.
        return True
    language = str(item.get("language") or "").lower()
    allowed = [str(x).lower() for x in item.get("allowedLanguages", [])] if isinstance(item.get("allowedLanguages"), list) else []
    return (language in {"cpp", "c++", "с++"} or "cpp" in allowed or "c++" in allowed or "с++" in allowed) and bool(item.get("referenceSolutionCpp"))


def _description_is_step_by_step(item: Dict[str, Any]) -> bool:
    text = str(item.get("description") or "").lower()
    return ("следуй шагам" in text or "следующие шаг" in text or "шагам" in text) and ("1." in text or "1)" in text)


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
            expects_cpp = _expects_cpp(context)
            cpp_invalid = 0
            style_requested = any(x in str(context.user_message or "").lower() for x in ["лесен", "пошаг", "каждым шаг", "задача 1", "задание 1"])
            style_invalid = 0
            for index, task in enumerate(tasks):
                if not isinstance(task, dict):
                    continue
                if expects_cpp and not _task_is_cpp(task):
                    warnings.append(f"task[{index}] is not C++ although context expects C++")
                    cpp_invalid += 1
                if style_requested and not _description_is_step_by_step(task):
                    warnings.append(f"task[{index}] description is not step-by-step training style")
                    style_invalid += 1
            if expects_cpp and cpp_invalid >= max(1, len(tasks) // 2):
                return _fail_result(result, "Лесенка отклонена: контекст требует C++, но LLM сгенерировал задачи не в C++.", warnings, validation)
            if style_requested and style_invalid >= max(1, len(tasks) // 2):
                return _fail_result(result, "Лесенка отклонена: задачи не оформлены как пошаговая обучалка в стиле заданий 1/1.1.", warnings, validation)
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


        if result.type == "polished_assignment_draft":
            assignment_type = str(result.data.get("assignmentType") or result.data.get("type") or "code-test").lower().replace("_", "-")
            if assignment_type == "polished-assignment-draft":
                assignment_type = "code-test"
            validation.setdefault("assignmentType", assignment_type)
            if assignment_type == "image-test":
                return _fail_result(result, "Вылизанный черновик отклонён: задачи на картинки в AI-пайплайне отключены.", warnings, validation)
            if not result.data.get("title") or not result.data.get("description"):
                return _fail_result(result, "Вылизанный черновик отклонён: нет title/description.", warnings, validation)
            if assignment_type == "code-test":
                tests = (result.data.get("publicTests") or []) + (result.data.get("hiddenTests") or [])
                validation.setdefault("testCount", len(tests))
                if len(tests) < 2:
                    return _fail_result(result, "Вылизанный code-test черновик отклонён: мало тестов.", warnings, validation)
                if _expects_cpp(context) or str(result.data.get("language") or "").lower() in {"cpp", "c++"}:
                    if not _task_is_cpp(result.data):
                        return _fail_result(result, "Вылизанный code-test черновик отклонён: нужен C++ и referenceSolutionCpp.", warnings, validation)
                runner_validation = result.data.get("runnerValidation") if isinstance(result.data.get("runnerValidation"), dict) else {}
                validation.setdefault("runnerUsed", bool(runner_validation.get("runnerUsed")))
                validation.setdefault("runnerPassed", bool(runner_validation.get("passed")))
            elif assignment_type == "test":
                spec = result.data.get("testSpec") if isinstance(result.data.get("testSpec"), dict) else {}
                questions = spec.get("questions") if isinstance(spec.get("questions"), list) else []
                validation.setdefault("questionCount", len(questions))
                if not questions:
                    return _fail_result(result, "Вылизанный test черновик отклонён: нет testSpec.questions.", warnings, validation)
            elif assignment_type == "math":
                spec = result.data.get("mathSpec") if isinstance(result.data.get("mathSpec"), dict) else {}
                blocks = spec.get("blocks") if isinstance(spec.get("blocks"), list) else []
                validation.setdefault("blockCount", len(blocks))
                if not blocks:
                    return _fail_result(result, "Вылизанный math черновик отклонён: нет mathSpec.blocks.", warnings, validation)
            else:
                return _fail_result(result, f"Вылизанный черновик отклонён: неподдерживаемый тип задания {assignment_type}.", warnings, validation)
            if assignment_type == "code-test" and not _description_is_step_by_step(result.data):
                return _fail_result(result, "Вылизанный code-test черновик отклонён: описание не похоже на пошаговую обучалку.", warnings, validation)


        if result.type == "assignment_update_batch":
            assignments = result.data.get("assignments") or []
            order = result.data.get("order") or []
            validation.setdefault("assignmentUpdateCount", len(assignments) if isinstance(assignments, list) else 0)
            validation.setdefault("orderCount", len(order) if isinstance(order, list) else 0)
            if not isinstance(assignments, list):
                return _fail_result(result, "Пакет правок отклонён: assignments должен быть массивом.", warnings, validation)
            if not isinstance(order, list):
                return _fail_result(result, "Пакет правок отклонён: order должен быть массивом id.", warnings, validation)
            editable = context.raw_payload.get("editableAssignments") if isinstance(context.raw_payload.get("editableAssignments"), list) else []
            known_ids = {str(x.get("id")) for x in editable if isinstance(x, dict) and x.get("id")}
            if known_ids:
                unknown = []
                for item in assignments:
                    if isinstance(item, dict) and item.get("id") and str(item.get("id")) not in known_ids:
                        unknown.append(str(item.get("id")))
                for item in order:
                    if str(item) not in known_ids:
                        unknown.append(str(item))
                if unknown:
                    return _fail_result(result, "Пакет правок отклонён: LLM вернул неизвестные assignment id.", warnings + unknown[:5], validation)
            if not assignments and not order and not result.data.get("normalizeRatings"):
                return _fail_result(result, "Пакет правок отклонён: нет ни assignments, ни order, ни normalizeRatings.", warnings, validation)

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
