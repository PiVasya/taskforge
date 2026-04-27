from __future__ import annotations

from typing import Any, Dict, List, Optional

from agent_core.context_pack import build_ai_context
from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient, compact_json
from api_client import AgentApiClient
from scenarios.base import Scenario, llm_failed_result
from scenarios.llm_common import BASE_SYSTEM, TASKFORGE_TRAINING_TASK_STYLE, as_dict, as_list, as_str


SUPPORTED_ASSIGNMENT_TYPES = {"code-test", "test", "math"}


def _request(context: AgentContextSnapshot) -> Dict[str, Any]:
    value = context.raw_payload.get("request")
    return value if isinstance(value, dict) else {}


def _selected_task(context: AgentContextSnapshot) -> Dict[str, Any]:
    req = _request(context)
    task = req.get("selectedTask") or req.get("task")
    return task if isinstance(task, dict) else {}


def _normalize_assignment_type(value: Any, *, task: Dict[str, Any] | None = None) -> str:
    task = task or {}
    explicit = str(value or task.get("assignmentType") or task.get("assignment_type") or task.get("taskType") or task.get("type") or "").strip().lower()
    explicit = explicit.replace("_", "-").replace(" ", "-")
    if explicit in {"code", "coding", "programming", "code-test"}:
        return "code-test"
    if explicit in {"quiz", "question", "questions", "text", "test", "task-test", "test-task"}:
        return "test"
    if explicit in {"math", "maths", "math-test", "formula", "numeric"}:
        return "math"
    if explicit in {"image", "image-test", "picture"}:
        return "image-test"

    haystack = compact_json(task, 6000).lower()
    if any(k in task for k in ["testSpec", "questions"]):
        return "test"
    if any(k in task for k in ["mathSpec", "blocks"]):
        return "math"
    if any(k in task for k in ["publicTests", "hiddenTests", "testCases", "referenceSolutionCpp", "referenceSolutionPython"]):
        return "code-test"
    if any(word in haystack for word in ["single-choice", "multi-choice", "вариант", "тест", "вопрос", "acceptedanswers"]):
        return "test"
    if any(word in haystack for word in ["матем", "формул", "expression", "matchpairs", "orderitems", "числовой ответ"]):
        return "math"
    return "code-test"


def _language_from_task(task: Dict[str, Any], context: AgentContextSnapshot) -> str:
    explicit = str(task.get("language") or "").lower()
    allowed = [str(x).lower() for x in task.get("allowedLanguages", [])] if isinstance(task.get("allowedLanguages"), list) else []
    haystack = compact_json({"task": task, "courseTitle": context.course_title, "message": context.user_message}, 3000).lower()
    if explicit in {"cpp", "c++"} or "cpp" in allowed or "c++" in allowed or "с++" in haystack or "c++" in haystack:
        return "cpp"
    if explicit in {"python", "py"} or "python" in allowed:
        return "python"
    if explicit in {"javascript", "js"} or "javascript" in allowed or "js" in allowed:
        return "javascript"
    if explicit in {"csharp", "c#", "cs"} or "csharp" in allowed or "c#" in allowed:
        return "csharp"
    if explicit in {"pascal", "pas"} or "pascal" in allowed:
        return "pascal"
    if explicit == "java" or "java" in allowed:
        return "java"
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


def _normalize_language_fields(data: Dict[str, Any], language: str, assignment_type: str) -> Dict[str, Any]:
    data["assignmentType"] = assignment_type
    if assignment_type == "code-test":
        data["language"] = language
        data["allowedLanguages"] = [language]
        if language == "cpp":
            data["referenceSolutionPython"] = None
    else:
        data.setdefault("language", language)
        data.setdefault("allowedLanguages", [language])
        data["runnerValidation"] = {"runnerUsed": False, "passed": None, "reason": f"{assignment_type} is validated structurally, not by code runner"}
    return data


def _schema_for(assignment_type: str, language: str) -> Dict[str, Any]:
    common: Dict[str, Any] = {
        "type": "polished_assignment_draft",
        "title": "string",
        "description": "full rich task text in TaskForge style",
        "assignmentType": assignment_type,
        "language": language,
        "allowedLanguages": [language],
        "difficulty": 1,
        "tags": "string",
        "selectedCourseId": "string|null",
        "selectedCourseTitle": "string|null",
        "beforeAssignmentId": "string|null",
        "afterAssignmentId": "string|null",
        "sourceTaskIndex": "number|null",
        "qualityNotes": ["string"],
        "warnings": ["string"],
    }
    if assignment_type == "code-test":
        common.update({
            "publicTests": [{"input": "string", "expectedOutput": "string"}],
            "hiddenTests": [{"input": "string", "expectedOutput": "string"}],
            "referenceSolutionCpp": "string|null",
            "referenceSolutionPython": "string|null",
            "runnerValidation": {"attempts": []},
        })
    elif assignment_type == "test":
        common.update({
            "testSpec": {
                "settings": {"maxAttempts": 1, "passPercent": 60, "shuffleQuestions": True, "shuffleAnswers": True, "allowReview": True, "attemptTimeLimitsSeconds": []},
                "questions": [{
                    "order": 0,
                    "type": "single-choice|multi-choice|fill|text",
                    "prompt": "string",
                    "options": [{"key": "a", "text": "string"}],
                    "correctOptionKeys": ["a"],
                    "acceptedAnswers": ["string"],
                    "caseSensitive": False,
                    "trim": True,
                }],
            }
        })
    elif assignment_type == "math":
        common.update({
            "mathSpec": {
                "settings": {"maxAttempts": 1, "passPercent": 60, "shuffleBlocks": False, "allowReview": True, "attemptTimeLimitsSeconds": []},
                "blocks": [{
                    "order": 0,
                    "kind": "info|number|expression|set|single-choice|multi-choice|order|match",
                    "prompt": "string",
                    "score": 1,
                    "isRequired": True,
                    "options": [{"key": "a", "text": "string"}],
                    "correctOptionKeys": ["a"],
                    "acceptedAnswers": ["string"],
                    "numericTolerance": 0,
                    "orderItems": ["string"],
                    "matchLeftItems": [{"key": "l1", "text": "string"}],
                    "matchRightItems": [{"key": "r1", "text": "string"}],
                    "matchPairs": [{"leftKey": "l1", "rightKey": "r1"}],
                }],
            }
        })
    return common


def _validate_structural(data: Dict[str, Any], assignment_type: str) -> Dict[str, Any]:
    errors: List[str] = []
    if not as_str(data.get("title")) or not as_str(data.get("description")):
        errors.append("missing title or description")
    if assignment_type == "test":
        spec = as_dict(data.get("testSpec"))
        questions = [q for q in as_list(spec.get("questions")) if isinstance(q, dict)]
        if not questions:
            errors.append("testSpec.questions is empty")
        for index, q in enumerate(questions):
            q_type = str(q.get("type") or "single-choice").replace("_", "-")
            if not as_str(q.get("prompt")):
                errors.append(f"testSpec.questions[{index}].prompt is empty")
            if q_type in {"single-choice", "multi-choice"}:
                if len(as_list(q.get("options"))) < 2:
                    errors.append(f"testSpec.questions[{index}].options has fewer than 2 options")
                if not as_list(q.get("correctOptionKeys")):
                    errors.append(f"testSpec.questions[{index}].correctOptionKeys is empty")
            elif q_type in {"fill", "text"} and not as_list(q.get("acceptedAnswers")):
                errors.append(f"testSpec.questions[{index}].acceptedAnswers is empty")
    elif assignment_type == "math":
        spec = as_dict(data.get("mathSpec"))
        blocks = [b for b in as_list(spec.get("blocks")) if isinstance(b, dict)]
        if not blocks:
            errors.append("mathSpec.blocks is empty")
        for index, b in enumerate(blocks):
            kind = str(b.get("kind") or "number").replace("_", "-")
            if not as_str(b.get("prompt")):
                errors.append(f"mathSpec.blocks[{index}].prompt is empty")
            if kind in {"single-choice", "multi-choice"} and (len(as_list(b.get("options"))) < 2 or not as_list(b.get("correctOptionKeys"))):
                errors.append(f"mathSpec.blocks[{index}] choice block is incomplete")
            if kind in {"number", "expression", "set"} and not as_list(b.get("acceptedAnswers")):
                errors.append(f"mathSpec.blocks[{index}].acceptedAnswers is empty")
            if kind == "order" and len(as_list(b.get("orderItems"))) < 2:
                errors.append(f"mathSpec.blocks[{index}].orderItems has fewer than 2 items")
            if kind == "match" and (not as_list(b.get("matchLeftItems")) or not as_list(b.get("matchRightItems")) or not as_list(b.get("matchPairs"))):
                errors.append(f"mathSpec.blocks[{index}] match block is incomplete")
    return {"passed": not errors, "errors": errors, "contentValidatorUsed": True}


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
        pipeline=["llm_polish_assignment", "validate_assignment_payload", "runner_validate_code_if_needed", "return_polished_draft"],
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

        assignment_type = _normalize_assignment_type(None, task=task)
        if assignment_type == "image-test":
            return ScenarioResult(
                type="llm_generation_failed",
                scenario_id=self.id,
                summary="AI-черновики для задач на картинки пока отключены: этот апдейт поддерживает code-test, test и math.",
                data={"type": "llm_generation_failed", "message": "image-test is intentionally unsupported"},
                confidence=0,
                warnings=["image-test is intentionally unsupported"],
                validation={"llmUsed": False, "templateUsed": False},
            )

        language = _language_from_task(task, context)
        schema = _schema_for(assignment_type, language)
        user = (
            "Пользователь отметил галочкой AI-сгенерированное задание. Нужно вылизать его в готовый скрытый черновик курса.\n"
            "Поддерживаемые типы: code-test, test, math. Задачи на картинки НЕ создавай и НЕ подменяй картинками.\n"
            "Сохрани исходный тип задания, если он code-test/test/math. Для test верни testSpec.questions. Для math верни mathSpec.blocks.\n"
            "Учитывай весь курс: сначала сканируй courseMap/courseOutline/courseDigest, а не только focusAssignments. Если ищешь тему позднего модуля, не делай вывод по первым задачам.\n"
            "Если есть placement before/after и sourceTaskIndex — сохрани их, чтобы backend мог вставить пакет в правильном порядке.\n"
            "Для code-test нужны минимум 2 публичных и 2 скрытых теста, эталонное решение и runner-проверка. Для test/math runner не используется: нужна структурная проверка контента.\n"
            f"Язык курса/кода: {language}. Для code-test на C++ обязательно referenceSolutionCpp, allowedLanguages=['cpp'], referenceSolutionPython=null. Для test/math кодовое решение не требуется.\n"
            "Текст задания должен быть в стиле TaskForge: понятная обучалка, нормальные переносы, шаги или ясные блоки условия, без заглушек.\n\n"
            f"Эталон стиля обучающих задач:\n{TASKFORGE_TRAINING_TASK_STYLE}\n\n"
            f"Выбранное задание JSON:\n{compact_json(task, 16000)}\n\n"
            f"Request JSON:\n{compact_json(req, 10000)}\n\n"
            f"Контекст TaskForge:\n{build_ai_context(context, max_chars=78000)}\n\n"
            f"Схема результата:\n{schema}"
        )

        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=user, purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Черновик не вылизан")

        data = as_dict(parsed)
        data["type"] = "polished_assignment_draft"
        data.setdefault("title", as_str(task.get("title"), "AI-черновик задания"))
        data["assignmentType"] = _normalize_assignment_type(data.get("assignmentType"), task=task)
        if data["assignmentType"] not in SUPPORTED_ASSIGNMENT_TYPES:
            data["assignmentType"] = assignment_type if assignment_type in SUPPORTED_ASSIGNMENT_TYPES else "code-test"
        assignment_type = data["assignmentType"]
        data.setdefault("difficulty", int(task.get("difficulty") or 1))
        data.setdefault("tags", "ОАИП,C++,AI,черновик" if language == "cpp" else "AI,черновик")
        data.setdefault("warnings", [])
        data.setdefault("qualityNotes", [])
        data.setdefault("sourceTaskIndex", req.get("taskIndex") or task.get("index"))
        data.setdefault("selectedCourseId", context.course_id or req.get("courseId"))
        data.setdefault("selectedCourseTitle", context.course_title)
        data.setdefault("beforeAssignmentId", req.get("beforeAssignmentId") or (as_dict(task.get("placement")).get("beforeAssignmentId") if isinstance(task.get("placement"), dict) else None))
        data.setdefault("afterAssignmentId", req.get("afterAssignmentId") or (as_dict(task.get("placement")).get("afterAssignmentId") if isinstance(task.get("placement"), dict) else None))
        data = _normalize_language_fields(data, language, assignment_type)

        if assignment_type == "code-test":
            validation = self._validate_with_runner(data, language)
            data["runnerValidation"] = validation
            if not validation.get("passed") and validation.get("repairPrompt"):
                repaired = self._repair(data, validation, language, context, req)
                if repaired:
                    data = _normalize_language_fields(as_dict(repaired), language, assignment_type)
                    data["type"] = "polished_assignment_draft"
                    data.setdefault("selectedCourseId", context.course_id or req.get("courseId"))
                    data.setdefault("selectedCourseTitle", context.course_title)
                    data.setdefault("beforeAssignmentId", req.get("beforeAssignmentId"))
                    data.setdefault("afterAssignmentId", req.get("afterAssignmentId"))
                    data.setdefault("sourceTaskIndex", req.get("taskIndex") or task.get("index"))
                    validation = self._validate_with_runner(data, language)
                    data["runnerValidation"] = validation
        else:
            validation = _validate_structural(data, assignment_type)
            data["contentValidation"] = validation

        warnings = [str(x) for x in as_list(data.get("warnings"))]
        if not validation.get("passed"):
            warnings.append("Черновик не прошёл автоматическую проверку структуры/тестов; будет сохранён с предупреждением для ручной проверки.")

        if assignment_type == "code-test":
            detail = f"прогон раннером: {'успешно' if validation.get('passed') else 'есть замечания'}"
        elif assignment_type == "test":
            q_count = len(as_list(as_dict(data.get("testSpec")).get("questions")))
            detail = f"тестовые вопросы: {q_count}; структурная проверка: {'успешно' if validation.get('passed') else 'есть замечания'}"
        else:
            b_count = len(as_list(as_dict(data.get("mathSpec")).get("blocks")))
            detail = f"математические блоки: {b_count}; структурная проверка: {'успешно' if validation.get('passed') else 'есть замечания'}"

        summary = f"Вылизал задание «{data.get('title')}» ({assignment_type}): условие и данные черновика подготовлены; {detail}."
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
                "assignmentType": assignment_type,
                "runnerUsed": bool(data.get("runnerValidation", {}).get("runnerUsed")) if isinstance(data.get("runnerValidation"), dict) else False,
                "runnerPassed": bool(data.get("runnerValidation", {}).get("passed")) if isinstance(data.get("runnerValidation"), dict) else False,
                "contentValidatorUsed": assignment_type != "code-test",
                "contentValidatorPassed": bool(validation.get("passed")),
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
            "Эталонное решение/тесты code-test не прошли runner-проверку. Исправь ТОЛЬКО код/тесты/ожидаемые выводы, сохрани стиль условия, тему и язык.\n"
            f"Язык: {language}. Верни снова полный polished_assignment_draft JSON.\n\n"
            f"Текущий черновик:\n{compact_json(data, 22000)}\n\n"
            f"Результаты раннера/ошибка:\n{compact_json(validation, 16000)}\n\n"
            f"Request:\n{compact_json(req, 6000)}\n\n"
            f"Контекст:\n{build_ai_context(context, max_chars=46000)}"
        )
        try:
            return as_dict(LlmJsonClient().generate(system=BASE_SYSTEM, user=user, purpose=f"{self.id}.repair").data)
        except Exception:
            return None
