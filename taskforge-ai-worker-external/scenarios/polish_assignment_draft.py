from __future__ import annotations

from typing import Any, Dict, List, Optional

from agent_core.context_pack import build_ai_context
from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient, compact_json
from api_client import AgentApiClient
from scenarios.base import Scenario, llm_failed_result
from scenarios.llm_common import BASE_SYSTEM, TASKFORGE_TRAINING_TASK_STYLE, as_dict, as_list, as_str


def _request(context: AgentContextSnapshot) -> Dict[str, Any]:
    value = context.raw_payload.get("request")
    return value if isinstance(value, dict) else {}


def _selected_task(context: AgentContextSnapshot) -> Dict[str, Any]:
    req = _request(context)
    task = req.get("selectedTask") or req.get("task")
    return task if isinstance(task, dict) else {}


def _language_from_task(task: Dict[str, Any], context: AgentContextSnapshot) -> str:
    explicit = str(task.get("language") or "").lower()
    allowed = task.get("allowedLanguages") if isinstance(task.get("allowedLanguages"), list) else []
    haystack = compact_json({"task": task, "courseTitle": context.course_title, "message": context.user_message}, 3000).lower()
    if explicit in {"cpp", "c++"} or "cpp" in allowed or "c++" in haystack or "с++" in haystack:
        return "cpp"
    if explicit in {"python", "py"} or "python" in allowed:
        return "python"
    return "cpp"


def _tests_from(data: Dict[str, Any]) -> List[Dict[str, Any]]:
    tests: List[Dict[str, Any]] = []
    for name, hidden in (("publicTests", False), ("hiddenTests", True), ("testCases", False)):
        for item in as_list(data.get(name)):
            if not isinstance(item, dict):
                continue
            tests.append({
                "input": str(item.get("input") or ""),
                "expectedOutput": str(item.get("expectedOutput") or item.get("output") or ""),
                "isHidden": bool(item.get("isHidden") if "isHidden" in item else item.get("hidden", hidden)),
            })
    return tests


def _normalize_language_fields(data: Dict[str, Any], language: str) -> Dict[str, Any]:
    data["language"] = language
    data["allowedLanguages"] = [language]
    if language == "cpp":
        data["referenceSolutionPython"] = None
    return data


class PolishAssignmentDraftScenario(Scenario):
    definition = ScenarioDefinition(
        id="polish_assignment_draft",
        name="Вылизывание выбранного AI-задания в скрытый черновик",
        family="publication-prep",
        aliases=["вылизать", "создать черновик", "скрытый черновик", "проверить тесты", "прогнать решение"],
        anti_aliases=[],
        default_count=1,
        default_mode="polish-draft",
        required_context=["selectedTask", "course_context", "chat_history"],
        pipeline=["llm_polish_assignment", "runner_validate_reference_solution", "llm_repair_if_needed", "return_polished_draft"],
        output_type="polished_assignment_draft",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        req = _request(context)
        task = _selected_task(context)
        if not task:
            return ScenarioResult(
                type="llm_generation_failed",
                scenario_id=self.id,
                summary="Не найдено выбранное AI-задание для вылизывания.",
                data={"type": "llm_generation_failed", "message": "selectedTask is missing"},
                confidence=0,
                warnings=["selectedTask is missing"],
                validation={"llmUsed": False, "templateUsed": False},
            )

        language = _language_from_task(task, context)
        schema = {
            "type": "polished_assignment_draft",
            "title": "string",
            "description": "full step-by-step task text",
            "assignmentType": "code-test",
            "language": language,
            "allowedLanguages": [language],
            "difficulty": 1,
            "tags": "string",
            "selectedCourseId": "string|null",
            "selectedCourseTitle": "string|null",
            "beforeAssignmentId": "string|null",
            "afterAssignmentId": "string|null",
            "sourceTaskIndex": "number|null",
            "publicTests": [{"input": "string", "expectedOutput": "string"}],
            "hiddenTests": [{"input": "string", "expectedOutput": "string"}],
            "referenceSolutionCpp": "string|null",
            "referenceSolutionPython": "string|null",
            "runnerValidation": {"attempts": []},
            "qualityNotes": ["string"],
            "warnings": ["string"],
        }
        user = (
            "Пользователь отметил галочкой AI-сгенерированное задание. Нужно не болтать, а вылизать его в готовый скрытый черновик курса.\n"
            "Сделай полноценное условие, тесты и эталонное решение. Затем результат будет сохранён как IsHidden=true, LifecycleStatus=ready, IsAiDraft=true.\n"
            "Учитывай весь чат, предыдущие артефакты, gap-аудит и текущий курс. Если есть placement before/after — сохрани его.\n"
            f"Язык строго: {language}. Для C++ обязательно referenceSolutionCpp, allowedLanguages=['cpp'], referenceSolutionPython=null.\n"
            "Текст задания должен быть в стиле начальных задач TaskForge: длинная понятная обучалка, 'Следуй шагам:', прямые строки/конструкции, пояснения в скобках.\n"
            "Тесты: минимум 2 публичных и 2 скрытых. Они должны быть осмысленными и проверять разные ветки.\n"
            "Не используй заглушки. Не сохраняй сам; только верни polished_assignment_draft.\n\n"
            f"Эталон стиля:\n{TASKFORGE_TRAINING_TASK_STYLE}\n\n"
            f"Выбранное задание JSON:\n{compact_json(task, 12000)}\n\n"
            f"Request JSON:\n{compact_json(req, 8000)}\n\n"
            f"Контекст TaskForge:\n{build_ai_context(context, max_chars=62000)}\n\n"
            f"Схема результата:\n{schema}"
        )

        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=user, purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Черновик не вылизан")

        data = _normalize_language_fields(as_dict(parsed), language)
        data["type"] = "polished_assignment_draft"
        data.setdefault("title", as_str(task.get("title"), "AI-черновик задания"))
        data.setdefault("assignmentType", "code-test")
        data.setdefault("difficulty", int(task.get("difficulty") or 1))
        data.setdefault("tags", "ОАИП,C++,AI,черновик" if language == "cpp" else "AI,черновик")
        data.setdefault("warnings", [])
        data.setdefault("qualityNotes", [])
        data.setdefault("sourceTaskIndex", req.get("taskIndex") or task.get("index"))
        data.setdefault("selectedCourseId", context.course_id or req.get("courseId"))
        data.setdefault("selectedCourseTitle", context.course_title)
        data.setdefault("beforeAssignmentId", req.get("beforeAssignmentId") or (as_dict(task.get("placement")).get("beforeAssignmentId") if isinstance(task.get("placement"), dict) else None))
        data.setdefault("afterAssignmentId", req.get("afterAssignmentId") or (as_dict(task.get("placement")).get("afterAssignmentId") if isinstance(task.get("placement"), dict) else None))

        validation = self._validate_with_runner(data, language)
        data["runnerValidation"] = validation

        if not validation.get("passed") and validation.get("repairPrompt"):
            repaired = self._repair(data, validation, language, context, req)
            if repaired:
                data = _normalize_language_fields(repaired, language)
                data["type"] = "polished_assignment_draft"
                data.setdefault("selectedCourseId", context.course_id or req.get("courseId"))
                data.setdefault("selectedCourseTitle", context.course_title)
                data.setdefault("beforeAssignmentId", req.get("beforeAssignmentId"))
                data.setdefault("afterAssignmentId", req.get("afterAssignmentId"))
                validation = self._validate_with_runner(data, language)
                data["runnerValidation"] = validation

        warnings = [str(x) for x in as_list(data.get("warnings"))]
        if not validation.get("passed"):
            warnings.append("Эталонное решение не прошло все тесты после попытки ремонта; черновик будет сохранён с предупреждением для ручной проверки.")

        summary = f"Вылизал задание «{data.get('title')}»: условие, тесты и эталонное решение подготовлены; прогон раннером: {'успешно' if validation.get('passed') else 'есть замечания'}."
        return ScenarioResult(
            type="polished_assignment_draft",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=88 if validation.get("passed") else 68,
            warnings=warnings,
            validation={
                "pipeline": self.definition.pipeline,
                "llmUsed": True,
                "templateUsed": False,
                "runnerUsed": bool(validation.get("runnerUsed")),
                "runnerPassed": bool(validation.get("passed")),
                "runnerAttempts": len(validation.get("attempts") or []),
                "willCreateHiddenDraft": True,
            },
        )

    def _validate_with_runner(self, data: Dict[str, Any], language: str) -> Dict[str, Any]:
        code = as_str(data.get("referenceSolutionCpp") if language == "cpp" else data.get("referenceSolutionPython"))
        tests = _tests_from(data)
        attempts: List[Dict[str, Any]] = []
        if not code or not tests:
            return {"runnerUsed": False, "passed": False, "attempts": attempts, "repairPrompt": "missing code or tests"}
        try:
            response = AgentApiClient().run_tests(language=language, code=code, test_cases=tests)
            attempts.append(response)
            passed = bool(response.get("ok"))
            return {"runnerUsed": True, "passed": passed, "attempts": attempts, "repairPrompt": None if passed else compact_json(response, 12000)}
        except Exception as exc:
            return {"runnerUsed": False, "passed": False, "attempts": attempts, "error": str(exc), "repairPrompt": str(exc)}

    def _repair(self, data: Dict[str, Any], validation: Dict[str, Any], language: str, context: AgentContextSnapshot, req: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        user = (
            "Эталонное решение/тесты не прошли runner-проверку. Исправь ТОЛЬКО код/тесты/ожидаемые выводы, сохрани стиль условия, тему и язык.\n"
            f"Язык: {language}. Верни снова полный polished_assignment_draft JSON.\n\n"
            f"Текущий черновик:\n{compact_json(data, 22000)}\n\n"
            f"Результаты раннера/ошибка:\n{compact_json(validation, 16000)}\n\n"
            f"Request:\n{compact_json(req, 6000)}\n\n"
            f"Контекст:\n{build_ai_context(context, max_chars=36000)}"
        )
        try:
            return as_dict(LlmJsonClient().generate(system=BASE_SYSTEM, user=user, purpose=f"{self.id}.repair").data)
        except Exception:
            return None
