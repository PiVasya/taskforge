from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple

from agent_core.contracts import AgentContextSnapshot, ScenarioDefinition, ScenarioResult, ScenarioRoute
from agent_core.llm_json import LlmJsonClient
from scenarios.base import Scenario, llm_failed_result, requested_count, target_concept
from scenarios.llm_common import BASE_SYSTEM, TASKFORGE_TRAINING_TASK_STYLE, as_dict, as_list, as_str, context_user_block

def _course_id_title_from_context(context: AgentContextSnapshot) -> Tuple[Optional[str], Optional[str]]:
    if context.course_id:
        return str(context.course_id), context.course_title

    memory = context.raw_payload.get("memory") if isinstance(context.raw_payload.get("memory"), dict) else {}
    for obj in [
        memory,
        as_dict(memory.get("currentDraftBlueprint")),
        as_dict(memory.get("lastCourseAnalysis")),
    ]:
        cid = as_str(obj.get("selectedCourseId") or obj.get("courseId") or obj.get("activeCourseId"))
        title = as_str(obj.get("selectedCourseTitle") or obj.get("courseTitle") or obj.get("activeCourseTitle"))
        if cid:
            return cid, title or None

    selected_courses = as_list(as_dict(memory.get("lastCourseAnalysis")).get("selectedCourses"))
    if len(selected_courses) == 1:
        course = as_dict(selected_courses[0])
        cid = as_str(course.get("courseId") or course.get("id"))
        if cid:
            return cid, as_str(course.get("title") or course.get("courseTitle")) or None

    matched = as_list(context.raw_payload.get("matchedCourses"))
    if len(matched) == 1:
        course = as_dict(matched[0])
        cid = as_str(course.get("id") or course.get("courseId"))
        if cid:
            return cid, as_str(course.get("title") or course.get("courseTitle")) or None

    return None, None


def _ctx_course(ctx: Dict[str, Any]) -> Dict[str, Any]:
    return as_dict(ctx.get("course"))


def _ctx_course_id(ctx: Dict[str, Any]) -> str:
    course = _ctx_course(ctx)
    return as_str(ctx.get("courseId") or course.get("id") or course.get("Id"))


def _ctx_course_title(ctx: Dict[str, Any]) -> str:
    course = _ctx_course(ctx)
    return as_str(ctx.get("courseTitle") or course.get("title") or course.get("Title"))


def _looks_like_guid(value: Any) -> bool:
    text = as_str(value)
    return len(text) == 36 and text.count("-") == 4


def _assignment_text(item: Dict[str, Any]) -> str:
    values = [
        item.get("title"), item.get("Title"), item.get("name"),
        item.get("description"), item.get("descriptionPreview"), item.get("contentSummary"), item.get("tags"),
        " ".join(str(x) for x in as_list(item.get("conceptHints") or item.get("concepts"))),
    ]
    return " ".join(str(v) for v in values if v).lower()


def _placement_from_gap_audit(context: AgentContextSnapshot, course_id: Optional[str]) -> Dict[str, Optional[str]]:
    memory = context.raw_payload.get("memory") if isinstance(context.raw_payload.get("memory"), dict) else {}
    audit = as_dict(memory.get("lastGapAudit") or context.last_course_audit)
    for finding in as_list(audit.get("findings")):
        item = as_dict(finding)
        if course_id and as_str(item.get("courseId")) and as_str(item.get("courseId")) != str(course_id):
            continue
        # In gap findings beforeAssignmentId means the previous real task and
        # afterAssignmentId means the next real task. For creation placement we
        # need after=previous and before=next.
        after_id = item.get("beforeAssignmentId")
        before_id = item.get("afterAssignmentId")
        if _looks_like_guid(after_id) or _looks_like_guid(before_id):
            return {
                "afterAssignmentId": as_str(after_id) if _looks_like_guid(after_id) else None,
                "beforeAssignmentId": as_str(before_id) if _looks_like_guid(before_id) else None,
                "reason": as_str(item.get("reason")) or "Позиция взята из последнего gap-audit.",
            }
    return {}


def _tokens(text: str) -> List[str]:
    raw = "".join(ch.lower() if ch.isalnum() or ch in "_+-#" else " " for ch in as_str(text))
    stop = {
        "перед", "после", "до", "за", "надо", "нужно", "сделай", "создай", "добавь", "добавить",
        "появлением", "появления", "появление", "стартом", "началом", "концом", "темой", "тема",
        "блоком", "блок", "разделом", "раздел", "задачей", "задачи", "заданием", "задания",
        "простые", "простых", "задачки", "задачку", "обучалки", "обучалку", "курс", "курсе",
        "там", "где", "которые", "чтобы", "для", "про", "по", "на", "и", "или", "у", "в", "к", "ко",
        "the", "a", "an", "before", "after", "task", "tasks", "topic", "section", "module", "lesson",
    }
    return [x for x in raw.split() if len(x) > 1 and x not in stop]


def _assignment_id(item: Dict[str, Any]) -> Optional[str]:
    value = as_str(item.get("id") or item.get("Id") or item.get("assignmentId"))
    return value if _looks_like_guid(value) else None


def _phrase_after_marker(text: str, markers: List[str]) -> str:
    lower = as_str(text).lower()
    positions = [(lower.find(marker), marker) for marker in markers if lower.find(marker) >= 0]
    if not positions:
        return ""
    pos, marker = min(positions, key=lambda item: item[0])
    tail = lower[pos + len(marker):]
    for sep in ["\n", ".", ";", "!", "?", " но ", " чтобы ", " там ", " где "]:
        idx = tail.find(sep)
        if idx >= 0:
            tail = tail[:idx]
    return tail.strip(" :,-—–")


def _placement_from_user_anchor(context: AgentContextSnapshot, course_id: Optional[str]) -> Dict[str, Optional[str]]:
    """Resolve placement from natural-language anchors for any topic.

    Examples: "перед функциями", "после циклов", "before recursion",
    "вставь до теста по if". This is deliberately topic-agnostic: it
    extracts the anchor phrase from the user's words and searches the current
    course map/outline instead of hard-coding a specific course theme.
    """
    text = as_str(context.user_message)
    before_phrase = _phrase_after_marker(text, [
        "перед появлением", "перед стартом", "перед началом", "перед", "до", "before",
    ])
    after_phrase = _phrase_after_marker(text, [
        "после завершения", "после прохождения", "после", "after",
    ])

    relation = "before" if before_phrase else "after" if after_phrase else ""
    phrase = before_phrase or after_phrase
    query_tokens = _tokens(phrase)
    if not relation or not query_tokens:
        return {}

    contexts = [as_dict(x) for x in as_list(context.raw_payload.get("courseContexts") or context.course_contexts)]
    if course_id:
        contexts = [ctx for ctx in contexts if _ctx_course_id(ctx) == str(course_id)] or contexts

    best: Optional[Tuple[int, int, List[Dict[str, Any]]]] = None
    for ctx in contexts:
        assignments = [as_dict(x) for x in as_list(ctx.get("assignments"))]
        for index, assignment in enumerate(assignments):
            haystack = _assignment_text(assignment)
            score = sum(1 for token in query_tokens if token in haystack)
            if score <= 0:
                continue
            # Prefer assignments matching more anchor tokens; keep earliest item
            # for ties because anchors usually mean the first occurrence of a topic.
            if best is None or score > best[0]:
                best = (score, index, assignments)

    if best is None:
        return {}

    _, index, assignments = best
    current_id = _assignment_id(assignments[index])
    prev_id = _assignment_id(assignments[index - 1]) if index > 0 else None
    next_id = _assignment_id(assignments[index + 1]) if index + 1 < len(assignments) else None

    if relation == "before":
        return {
            "afterAssignmentId": prev_id,
            "beforeAssignmentId": current_id,
            "reason": f"Позиция определена по фразе пользователя: перед «{phrase}».",
        }

    return {
        "afterAssignmentId": current_id,
        "beforeAssignmentId": next_id,
        "reason": f"Позиция определена по фразе пользователя: после «{phrase}».",
    }


def _resolve_course_and_placement(context: AgentContextSnapshot, data: Dict[str, Any]) -> Tuple[Optional[str], Optional[str], Dict[str, Optional[str]]]:
    course_id = as_str(data.get("selectedCourseId") or data.get("courseId")) or None
    course_title = as_str(data.get("selectedCourseTitle") or data.get("courseTitle")) or None

    if not course_id:
        course_id, course_title = _course_id_title_from_context(context)

    if course_id and not course_title:
        for ctx in as_list(context.raw_payload.get("courseContexts") or context.course_contexts):
            c = as_dict(ctx)
            if _ctx_course_id(c) == str(course_id):
                course_title = _ctx_course_title(c) or course_title
                break

    placement = as_dict(data.get("placement"))
    if not (placement.get("beforeAssignmentId") or placement.get("afterAssignmentId")):
        placement = _placement_from_gap_audit(context, course_id) or _placement_from_user_anchor(context, course_id)

    return course_id, course_title, placement


def _apply_target_to_drafts(data: Dict[str, Any], course_id: Optional[str], course_title: Optional[str], placement: Dict[str, Optional[str]]) -> None:
    if course_id:
        data["selectedCourseId"] = course_id
    if course_title:
        data["selectedCourseTitle"] = course_title
    if placement:
        data["placement"] = placement

    for idx, draft in enumerate(as_list(data.get("drafts"))):
        if not isinstance(draft, dict):
            continue
        draft.setdefault("index", idx + 1)
        if course_id and not draft.get("selectedCourseId"):
            draft["selectedCourseId"] = course_id
        if course_title and not draft.get("selectedCourseTitle"):
            draft["selectedCourseTitle"] = course_title
        if placement and not draft.get("placement"):
            draft["placement"] = placement
        if placement.get("beforeAssignmentId") and not draft.get("beforeAssignmentId"):
            draft["beforeAssignmentId"] = placement.get("beforeAssignmentId")
        if placement.get("afterAssignmentId") and not draft.get("afterAssignmentId"):
            draft["afterAssignmentId"] = placement.get("afterAssignmentId")



class StyleMatchedTasksScenario(Scenario):
    definition = ScenarioDefinition(
        id="style_matched_tasks",
        name="Задачи в стиле курса",
        family="generation",
        aliases=["в стиле курса", "как текущие задачи", "похожие задачи", "сделай как здесь"],
        anti_aliases=["другим стилем"],
        default_count=3,
        default_mode="style-pack",
        required_context=["course_catalog", "course_contexts", "user_message"],
        pipeline=["llm_understand_request", "llm_generate_task_drafts", "validate_no_persistence"],
        output_type="task_draft_bundle",
        can_run_directly=True,
        needs_course=False,
    )

    def run(self, context: AgentContextSnapshot, route: Optional[ScenarioRoute], previous_results: List[ScenarioResult]) -> ScenarioResult:
        count = requested_count(route, self.definition.default_count, 1, 8)
        topic = target_concept(route, "general")
        schema = {
            "type": "task_draft_bundle",
            "title": "string",
            "summary": "string",
            "topic": topic,
            "language": "C++|Python|JavaScript|C#|Pascal|Java|unknown",
            "selectedCourseId": "string|null",
            "selectedCourseTitle": "string|null",
            "drafts": [{
                "title": "string",
                "assignmentType": "code-test|test|math",
                "language": "string",
                "allowedLanguages": ["string"],
                "inputMode": "graphical-editor|code-editor|text",
                "difficulty": 1,
                "description": "string",
                "publicTests": [{"input": "string", "expectedOutput": "string"}],
                "hiddenTests": [{"input": "string", "expectedOutput": "string"}],
                "referenceSolutionCpp": "string|null",
                "referenceSolutionPython": "string|null",
                "testSpec": {"settings": {}, "questions": [{"type": "single-choice|multi-choice|fill|text", "prompt": "string", "options": [{"key": "a", "text": "string"}], "correctOptionKeys": ["a"], "acceptedAnswers": ["string"]}]},
                "mathSpec": {"settings": {}, "blocks": [{"kind": "info|number|expression|set|single-choice|multi-choice|order|match", "prompt": "string", "acceptedAnswers": ["string"], "options": [{"key": "a", "text": "string"}], "correctOptionKeys": ["a"]}]},
                "pedagogicalGoal": "string",
                "targetSkill": "string",
                "prerequisites": ["string"],
                "newConcepts": ["string"],
                "forbiddenConcepts": ["string"],
                "styleNotes": ["string"],
                "validationNotes": ["llm generated", "not persisted"]
            }],
            "warnings": ["string"],
        }
        task = f"""
Создай {count} качественных черновиков задач по запросу пользователя. Если в контексте есть selectedCourseId/activeCourseId или предыдущий аудит курса, обязательно укажи selectedCourseId, selectedCourseTitle и placement для будущего сохранения. Если пользователь указывает позицию через фразы вроде "перед <темой>", "до <раздела>", "после <задания>", placement должен указывать позицию относительно найденного якоря в курсе. Не привязывайся к одной конкретной теме: одинаково поддерживай матрицы, функции, циклы, строки, классы, тесты и любые другие темы из courseMap/courseContexts.
Тема/навык: {topic}.
Не сохраняй в курс на этом шаге: верни blueprint. Но blueprint должен содержать все данные для последующего сохранения скрытых черновиков в конкретный курс. Не используй шаблон-заглушку. Каждая задача должна быть реально осмысленной и отличаться от остальных.
Если задача типа code-test и контекст указывает C++/Основы C++, строго сохраняй C++: language='cpp', allowedLanguages=['cpp'], referenceSolutionCpp заполнен, referenceSolutionPython=null. Для test/math заполняй testSpec/mathSpec, а кодовое решение не требуется.
Если пользователь просит стиль первых обучающих заданий, используй этот эталон:
{TASKFORGE_TRAINING_TASK_STYLE}
Если задача типа code-test и тема if/условия, эталонное решение обязано реально содержать if. Не генерируй image/image-test: задачи на картинки выключены.
"""
        try:
            parsed = LlmJsonClient().generate(system=BASE_SYSTEM, user=context_user_block(context, task, schema), purpose=self.id).data
        except Exception as exc:
            return llm_failed_result(self.id, exc, title="Генерация задач не выполнена")

        data = as_dict(parsed)
        data["type"] = "task_draft_bundle"
        data.setdefault("title", f"Пакет задач: {topic}")
        data.setdefault("topic", topic)
        data.setdefault("drafts", [])
        data.setdefault("warnings", [])
        course_id, course_title, placement = _resolve_course_and_placement(context, data)
        _apply_target_to_drafts(data, course_id, course_title, placement)
        warnings = [str(x) for x in as_list(data.get("warnings"))]
        if not course_id:
            warnings.append("Не удалось однозначно определить курс для будущего сохранения. Кнопки сохранения должны быть недоступны, пока курс не выбран.")
            data["warnings"] = warnings
        elif not placement:
            warnings.append("Курс определён, но позиция вставки не найдена: скрытые черновики будут добавлены в конец курса, если пользователь отправит их на вылизывание.")
            data["warnings"] = warnings
        summary = as_str(data.get("summary"), f"LLM подготовил {len(as_list(data.get('drafts')))} черновиков задач.")
        return ScenarioResult(
            type="task_draft_bundle",
            scenario_id=self.id,
            summary=summary,
            data=data,
            confidence=84,
            warnings=warnings,
            validation={"pipeline": self.definition.pipeline, "llmUsed": True, "templateUsed": False, "noPersistence": "ok", "selectedCourseId": course_id, "placementResolved": bool(placement)},
        )
